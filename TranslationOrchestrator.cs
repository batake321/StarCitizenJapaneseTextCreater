using System.Collections.Concurrent;
using System.Text.Json;

namespace StarCitizenJapaneseTextCreater;

public class TranslationOrchestrator
{
    private readonly List<TranslationBackend> _backends;
    private readonly int _maxRetries;
    private readonly string _untranslatedPath;
    private readonly string _translatedPath;
    private readonly ProgressTracker _progress;
    private readonly object _writeLock = new();
    private int _successTotal;
    private int _fallbackTotal;
    private int _totalEntries;
    private int _retryDone, _retryTotal;   // 送り直しの済んだ数・全体の数 (進み具合の表示用。_progress には足さない)
    private int _nameBrokenTotal;   // 名前の置き換え記号 {nameN} が合わず保存しなかった訳の数 (リトライ分も数える)
    private readonly ConcurrentBag<TranslationEntry> _failedEntries = new();
    private readonly ConcurrentBag<string> _keptEnglishKeys = new();

    // AI が英語原文をそのまま返したキー。翻訳後に「原文維持」として DB に記録し、次回から翻訳対象にしない
    public IReadOnlyCollection<string> KeptEnglishKeys => _keptEnglishKeys;

    public event Action<int, int, int, int>? ProgressChanged;
    public event Action<List<(string Key, string Japanese, string Translator)>>? BatchTranslated;

    public TranslationOrchestrator(
        List<TranslationBackend> backends,
        int maxRetries,
        string untranslatedPath,
        string translatedPath,
        ProgressTracker progress)
    {
        _backends = backends;
        _maxRetries = maxRetries;
        _untranslatedPath = untranslatedPath;
        _translatedPath = translatedPath;
        _progress = progress;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var entries = LoadEntries();
        _totalEntries = entries.Count;
        var startIdx = _progress.Done;
        var remaining = entries.Skip(startIdx).ToList();

        Console.WriteLine($"[{Now}] 全体: {entries.Count}, 完了済: {startIdx}, 残り: {remaining.Count}");
        Console.WriteLine($"[{Now}] バックエンド: {string.Join(", ", _backends.Select(b => $"{b.Name}({b.ModelName})"))}");

        if (remaining.Count == 0)
        {
            Console.WriteLine($"[{Now}] 翻訳対象がありません。");
            return;
        }

        using var outFile = new StreamWriter(_translatedPath, append: true, encoding: System.Text.Encoding.UTF8);

        await RunBatches(remaining, outFile, ct);

        // Retry failed entries
        if (_failedEntries.Count > 0 && !ct.IsCancellationRequested)
        {
            var retryList = _failedEntries.ToList();
            _failedEntries.Clear();
            Console.WriteLine($"[{Now}] --- リトライ: {retryList.Count} 件 ---");
            await RunBatches(retryList, outFile, ct, isRetry: true);
        }

        var stillFailed = _failedEntries.Count;
        Console.WriteLine($"[{Now}] 完了! 翻訳成功: {_successTotal}, 失敗: {stillFailed}");
        if (_nameBrokenTotal > 0)
            Console.WriteLine($"[{Now}] 名前の置き換え記号が合わず保存しなかった訳: {_nameBrokenTotal} 件 (未翻訳のまま。次の翻訳で送り直します)");
    }

    private async Task RunBatches(List<TranslationEntry> entries, StreamWriter outFile, CancellationToken ct, bool isRetry = false)
    {
        var perBackend = _backends.ToDictionary(b => b.Name, _ => new List<List<TranslationEntry>>());
        int pos = 0, backendIdx = 0;
        while (pos < entries.Count)
        {
            var backend = _backends[backendIdx % _backends.Count];
            var size = Math.Min(backend.BatchSize, entries.Count - pos);
            perBackend[backend.Name].Add(entries.GetRange(pos, size));
            pos += size;
            backendIdx++;
        }

        var totalBatches = perBackend.Values.Sum(q => q.Count);
        if (!isRetry)
            Console.WriteLine($"[{Now}] バッチ数: {totalBatches} ({string.Join(", ", _backends.Select(b => $"{b.Name}:{perBackend[b.Name].Count}"))})");

        if (isRetry) { _retryDone = 0; _retryTotal = entries.Count; }
        var tasks = _backends.Select(backend => ProcessBackendQueue(backend, perBackend[backend.Name], outFile, ct, isRetry));
        await Task.WhenAll(tasks);
    }

    private async Task ProcessBackendQueue(TranslationBackend backend, List<List<TranslationEntry>> batches, StreamWriter outFile, CancellationToken ct, bool isRetry)
    {
        int consecutiveFailures = 0;
        var lastRequestTime = DateTime.MinValue;
        var minInterval = backend is GeminiBackend ? 7000 : 0;

        for (int i = 0; i < batches.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (minInterval > 0)
            {
                var elapsed = (DateTime.UtcNow - lastRequestTime).TotalMilliseconds;
                if (elapsed < minInterval)
                {
                    var wait = (int)(minInterval - elapsed);
                    await Task.Delay(wait, ct);
                }
            }

            lastRequestTime = DateTime.UtcNow;
            bool success = await ProcessBatch(backend, batches[i], outFile, isRetry);
            if (success)
            {
                consecutiveFailures = 0;
            }
            else
            {
                consecutiveFailures++;
                var cooldown = backend is GeminiBackend
                    ? Math.Min(60 + consecutiveFailures * 30, 300)
                    : Math.Min(consecutiveFailures * 10, 120);
                Console.WriteLine($"[{Now}] [{backend.Name}] 連続{consecutiveFailures}回失敗 — {cooldown}秒クールダウン");
                await Task.Delay(cooldown * 1000, ct);
            }
        }
    }

    private async Task<bool> ProcessBatch(TranslationBackend backend, List<TranslationEntry> batch, StreamWriter outFile, bool isRetry)
    {
        Dictionary<string, string>? resultMap = null;

        // 文の中の名前を {nameN} に置き換えて送る (NameMasker)。返ってきた訳で元の名前に戻す
        var masks = new Dictionary<string, (string Masked, Dictionary<string, string> Map)>();
        var sendBatch = new List<TranslationEntry>(batch.Count);
        foreach (var entry in batch)
        {
            var (masked, map) = NameMasker.Mask(entry.English);
            if (map.Count > 0)
            {
                masks[entry.Key] = (masked, map);
                sendBatch.Add(new TranslationEntry { Key = entry.Key, English = masked });
            }
            else sendBatch.Add(entry);
        }

        for (int attempt = 0; attempt < _maxRetries; attempt++)
        {
            try
            {
                resultMap = await backend.TranslateAsync(sendBatch);
                break;
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("429"))
            {
                var jitter = Random.Shared.Next(0, 5);
                int delay;
                if (backend is GeminiBackend)
                    delay = (int)Math.Min(Math.Pow(2, attempt + 1) * 30, 300) + jitter;
                else
                    delay = (int)Math.Min(Math.Pow(2, attempt) * 15, 120) + jitter;
                Console.WriteLine($"[{Now}] [{backend.Name}] ERR attempt {attempt + 1}: 429 Rate Limited — {delay}秒待機");
                await Task.Delay(delay * 1000);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{Now}] [{backend.Name}] ERR attempt {attempt + 1}: {ex.Message}");
                if (attempt < _maxRetries - 1)
                    await Task.Delay(backend is ClaudeBackend or GeminiBackend ? 5000 : 3000);
            }
        }

        // 訳の中の {nameN} を元の名前に戻す。欠けている・余計にある・番号が壊れている訳は保存しない
        var nameBroken = new HashSet<string>();
        if (resultMap != null)
        {
            foreach (var (key, (masked, map)) in masks)
            {
                if (!resultMap.TryGetValue(key, out var tr) || string.IsNullOrWhiteSpace(tr)) continue;
                var restored = NameMasker.Unmask(masked, tr, map);
                if (restored == null) { nameBroken.Add(key); resultMap.Remove(key); }
                else resultMap[key] = restored;
            }
        }

        // AI が訳と一緒に返した人名 (キー → 人名の一覧。TranslationBackend.ParseResponse が読んだもの)
        var persons = (resultMap as TranslationBackend.TranslationResult)?.PersonNames;
        var personsToStore = new List<(string Name, string SourceKey)>();
        int personMissing = 0;

        var lines = new List<string>();
        var translatedItems = new List<(string Key, string Japanese, string Translator)>();
        int ok = 0, fail = 0, sameAsEnglish = 0;

        foreach (var entry in batch)
        {
            if (nameBroken.Contains(entry.Key))
            {
                // 名前の置き換え記号 {nameN} が欠けた・余計にある・番号が壊れた訳は保存せず失敗扱いにする
                fail++;
                _failedEntries.Add(entry);
                Console.WriteLine($"[{Now}] [{backend.Name}] 名前の置き換え記号が合わないため保存しません: {entry.Key}");
            }
            else if (resultMap != null && resultMap.TryGetValue(entry.Key, out var translated) && !string.IsNullOrWhiteSpace(translated)
                && translated.Trim() == entry.English.Trim())
            {
                // AI が英語原文をそのまま返したものは AI の訳としては保存せず、「原文維持」にする (翻訳後に DB へ記録。リトライもしない)
                sameAsEnglish++;
                _keptEnglishKeys.Add(entry.Key);
            }
            else if (resultMap != null && resultMap.TryGetValue(entry.Key, out translated) && !string.IsNullOrWhiteSpace(translated)
                && BrokenTranslationReason(entry.English, translated) is string brokenReason)
            {
                // 韓国語が混じった訳・トークンやタブが壊れた訳・強調タグが壊れた訳は保存せず失敗扱いにする (別の AI で訳し直される)
                fail++;
                _failedEntries.Add(entry);
                Console.WriteLine($"[{Now}] [{backend.Name}] {brokenReason}ため保存しません: {entry.Key}");
            }
            else if (resultMap != null && resultMap.TryGetValue(entry.Key, out translated) && !string.IsNullOrWhiteSpace(translated)
                && entry.English.Length > 200 && translated.Length < entry.English.Length * 0.2)
            {
                // 英語に比べて極端に短い訳は途中で切れたものとみなし、保存せず失敗扱いにする (別の AI で訳し直される)
                fail++;
                _failedEntries.Add(entry);
                Console.WriteLine($"[{Now}] [{backend.Name}] 訳が短すぎるため保存しません: {entry.Key} (英語 {entry.English.Length} 字 / 訳 {translated.Length} 字)");
            }
            else if (resultMap != null && resultMap.TryGetValue(entry.Key, out translated) && !string.IsNullOrWhiteSpace(translated)
                && MissingPersonName(persons, entry.Key, translated) is string missingName)
            {
                // AI が返した人名が訳の中に原語のまま入っていない (音訳・訳された) ものは保存せず失敗扱いにする (別の AI で訳し直される)
                fail++;
                personMissing++;
                _failedEntries.Add(entry);
                Console.WriteLine($"[{Now}] [{backend.Name}] 人名「{missingName}」が訳の中に原語のまま無いため保存しません: {entry.Key}");
            }
            else if (resultMap != null && resultMap.TryGetValue(entry.Key, out translated) && !string.IsNullOrWhiteSpace(translated))
            {
                // 保存する訳に付いていた人名のうち、貯めてよいもの (NameMasker.IsStorablePersonName) を名前の一覧に貯める
                if (persons != null && persons.TryGetValue(entry.Key, out var names))
                    foreach (var n in names)
                        if (NameMasker.IsStorablePersonName(n, entry.English)) personsToStore.Add((n, entry.Key));
                ok++;
                translatedItems.Add((entry.Key, translated, backend.TranslatorLabel));
                lines.Add(JsonSerializer.Serialize(
                    new { key = entry.Key, ja = translated, translator = backend.TranslatorLabel },
                    new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            }
            else
            {
                fail++;
                _failedEntries.Add(entry);
            }
        }

        lock (_writeLock)
        {
            foreach (var line in lines)
                outFile.WriteLine(line);
            outFile.Flush();

            _successTotal += ok;
            _fallbackTotal += fail;
            _nameBrokenTotal += nameBroken.Count;
            // 送り直し (isRetry) の分は全体の進み具合 (_progress。続きから再開するときの位置にも使う) に足さず、送り直しの中の数で表示する
            if (!isRetry) _progress.Update(batch.Count);
            else _retryDone += batch.Count;

            var done = _progress.Done;
            var pct = (double)done / _totalEntries * 100;
            var progressText = isRetry
                ? $"リトライ {_retryDone:N0}/{_retryTotal:N0} ({(double)_retryDone / _retryTotal * 100:F1}%)"
                : $"{done:N0}/{_totalEntries:N0} ({pct:F1}%)";
            Console.WriteLine($"[{Now}] [{backend.Name}] {progressText} OK:{ok} FAIL:{fail}"
                + (sameAsEnglish > 0 ? $" 原文のまま(未保存):{sameAsEnglish}" : "")
                + (masks.Count > 0 ? $" 名前の置き換え:{masks.Count}件" : "")
                + (nameBroken.Count > 0 ? $" 置き換え記号の不一致(未保存):{nameBroken.Count}" : "")
                + (personMissing > 0 ? $" 人名が原語のまま無い(未保存):{personMissing}" : ""));
            ProgressChanged?.Invoke(done, _totalEntries, _successTotal, _fallbackTotal);
        }

        // 人名を名前の一覧と翻訳 DB の person_names に貯める (次の Mask から {nameN} に置き換えて守る)
        if (personsToStore.Count > 0)
        {
            try
            {
                var added = NameMasker.AddPersonNames(personsToStore);
                if (added > 0)
                    Console.WriteLine($"[{Now}] [{backend.Name}] 人名を名前の一覧に足しました: {added} 件 ({string.Join(", ", personsToStore.Select(p => p.Name).Distinct().Take(10))})");
            }
            catch (Exception ex) { Console.WriteLine($"[{Now}] [{backend.Name}] 人名を貯められませんでした: {ex.Message}"); }
        }

        if (translatedItems.Count > 0)
            BatchTranslated?.Invoke(translatedItems);

        return ok + sameAsEnglish > 0;
    }

    private static string Now => DateTime.Now.ToString("HH:mm:ss");

    // AI がそのキーに返した人名のうち、訳 (名前を戻した後) の中に原語のまま (大文字小文字を区別) 入っていない最初のものを返す。
    // 全部入っていれば (人名が無ければ) null。{ を含むもの ({nameN} を人名として返したもの) は確かめない
    internal static string? MissingPersonName(Dictionary<string, List<string>>? persons, string key, string translated)
    {
        if (persons == null || !persons.TryGetValue(key, out var names)) return null;
        foreach (var n in names)
            if (!n.Contains('{') && !translated.Contains(n, StringComparison.Ordinal)) return n;
        return null;
    }

    private List<TranslationEntry> LoadEntries()
    {
        var entries = new List<TranslationEntry>();
        foreach (var line in File.ReadLines(_untranslatedPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var item = JsonSerializer.Deserialize<TranslationEntry>(line);
            if (item != null) entries.Add(item);
        }
        return entries;
    }

    private static readonly System.Text.RegularExpressions.Regex EmphasisTagRegex = new(@"</?EM[^>]*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex ValidEmphasisTagRegex = new(@"^</?EM[1-4]?>$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex HangulRegex = new(@"[가-힣]");
    // ~mission(…) ~action(…) 等のトークン (英語の global.ini のトークンの中には空白・タブが無い)
    private static readonly System.Text.RegularExpressions.Regex TokenRegex = new(@"~\w+\([^)]*\)");
    private static readonly System.Text.RegularExpressions.Regex WhitespaceRegex = new(@"\s");
    // %s %ls %d %i %u %f %S %02i %.2f 等の書式の記号 (英語の global.ini にある形。「%Is」は %ls の書き間違いとみられる形で原文にある)。
    // 「50% 」「%\n」のような割合の % には合わない
    private static readonly System.Text.RegularExpressions.Regex FormatSpecRegex = new(@"%\d*(?:\.\d+)?(?:ls|lS|Is|[sSdiuf])");
    // @xxx の記号 (例 "@General Navigation Light" の "@General")。訳で後ろに日本語が直接続いても記号だけを取るよう、英字・数字・_ に限る
    private static readonly System.Text.RegularExpressions.Regex AtTokenRegex = new(@"@[A-Za-z0-9_]+");

    // 文字列としての「\n」(「\」と「n」の 2 文字) の数
    private static int CountEscapedNewlines(string s)
    {
        int n = 0;
        for (int i = s.IndexOf("\\n", StringComparison.Ordinal); i >= 0; i = s.IndexOf("\\n", i + 2, StringComparison.Ordinal)) n++;
        return n;
    }

    // 文字列ごとの出てくる数
    private static Dictionary<string, int> CountByValue(IEnumerable<string> values)
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var v in values) d[v] = d.TryGetValue(v, out var c) ? c + 1 : 1;
        return d;
    }

    private static bool SameCounts(Dictionary<string, int> a, Dictionary<string, int> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var c) && c == kv.Value);

    // 保存してはいけない訳なら理由を返す。問題なければ null
    // ・原文に無い韓国語の文字が混じっている (AI の誤り)
    // ・~mission( ) 等のトークンの中に空白・タブが入っている (原文に同じトークンがあるものは除く。例 ~mission(MissionMaxSCU\t\t\t\t\tSize))
    // ・原文より多くタブが入っている。応答は 1 行が「キー\t訳」の形で、最初のタブより後ろを訳として読むため、
    //   AI が「キー\t英語原文\t訳」と原文を写して返すと「原文\t訳」が訳になる (実例 'Anti-Air\t対空防御')。
    //   ほかの項目の原文・訳がタブでつながったものも同じ。英語の global.ini にも生のタブがある値があり (30 件)、
    //   その訳がタブを写すのは正しいので、原文のタブの数までは通す
    // ・強調タグの名前が壊れている (</EMCA> など)、または開きと閉じの数が合わない
    //   (原文の閉じ忘れを AI が補ったものは、名前が正しく数が合っているので通す)
    // ・原文にある ~mission( ) 等のトークン・%s 等の書式の記号・@xxx の記号・強調タグが欠けている、または数が違う
    //   (強調タグが閉じていない原文は、訳のタグの総数が原文より少ないときだけ)
    // ・文字列としての「\n」の数が原文と違う
    internal static string? BrokenTranslationReason(string english, string translated)
    {
        if (HangulRegex.IsMatch(translated) && !HangulRegex.IsMatch(english))
            return "韓国語の文字が混じっている";
        if (TokenRegex.Matches(translated).Any(m => WhitespaceRegex.IsMatch(m.Value) && !english.Contains(m.Value, StringComparison.Ordinal)))
            return "トークンの中に空白・タブが入っている";
        if (translated.Count(c => c == '\t') > english.Count(c => c == '\t'))
            // 原文は前後の空白を除いて比べる (英語が空白で終わっていて AI がその空白も写したもの。例 item_Descksar_gren_frag_01)
            return english.Trim().Length > 0
                   && System.Text.RegularExpressions.Regex.IsMatch(translated, "^" + System.Text.RegularExpressions.Regex.Escape(english.Trim()) + @"[^\S\t]*\t")
                ? "英語の原文が前に付いている"
                : "タブが入っている";
        var tags = EmphasisTagRegex.Matches(translated).Select(m => m.Value).ToList();
        if (tags.Count > 0)
        {
            if (tags.Any(t => !ValidEmphasisTagRegex.IsMatch(t)))
                return "強調タグが壊れている";
            var opens = tags.Count(t => !t.StartsWith("</"));
            // 原文のタグがもともと閉じていない (CIG 側の誤り) ものは、訳がそれを写していても弾かない
            var srcTags = EmphasisTagRegex.Matches(english).Select(m => m.Value).ToList();
            var srcOpens = srcTags.Count(t => !t.StartsWith("</"));
            if (opens != tags.Count - opens && srcOpens == srcTags.Count - srcOpens)
                return "強調タグの開きと閉じの数が合わない";
        }
        // ~mission(…) 等のトークンが欠けている・数が違う (トークンごとに原文と同じ数だけあるか)
        if (!SameCounts(CountByValue(TokenRegex.Matches(english).Select(m => m.Value)),
                        CountByValue(TokenRegex.Matches(translated).Select(m => m.Value))))
            return "~mission( ) 等のトークンが欠けている・数が違う";
        // %s %ls %d 等の書式の記号、@xxx の記号が欠けている・数が違う (記号ごとに原文と同じ数だけあるか)
        if (!SameCounts(CountByValue(FormatSpecRegex.Matches(english).Select(m => m.Value)),
                        CountByValue(FormatSpecRegex.Matches(translated).Select(m => m.Value))))
            return "%s 等の書式の記号が欠けている・数が違う";
        if (!SameCounts(CountByValue(AtTokenRegex.Matches(english).Select(m => m.Value)),
                        CountByValue(AtTokenRegex.Matches(translated).Select(m => m.Value))))
            return "@ の記号が欠けている・数が違う";
        // 強調タグが欠けている・数が違う (タグごとに原文と同じ数だけあるか。大文字小文字は区別しない (上の検査と同じ))。
        // 原文のタグがもともと閉じていないものは、AI が閉じ忘れを補うと数が変わるので同じ数かは比べず、
        // 訳のタグの総数が原文より少ないときだけ弾く (閉じ忘れを補うと総数は同じか多くなる)
        {
            var srcTags = EmphasisTagRegex.Matches(english).Select(m => m.Value.ToUpperInvariant()).ToList();
            var srcOpens = srcTags.Count(t => !t.StartsWith("</"));
            var trTags = EmphasisTagRegex.Matches(translated).Select(m => m.Value.ToUpperInvariant()).ToList();
            if (srcOpens == srcTags.Count - srcOpens
                ? !SameCounts(CountByValue(srcTags), CountByValue(trTags))
                : trTags.Count < srcTags.Count)
                return "強調タグが欠けている・数が違う";
        }
        // 文字列としての「\n」の数が原文と違う
        if (CountEscapedNewlines(english) != CountEscapedNewlines(translated))
            return "\\n の数が原文と違う";
        return null;
    }

    public static void BuildUntranslatedList(
        Dictionary<string, string> english,
        Dictionary<string, string> japanese,
        string outputPath,
        List<string> forceEnglishPatterns,
        string? dbPath = null)
    {
        var forceEnglishRegex = forceEnglishPatterns
            .Select(p => new System.Text.RegularExpressions.Regex(p))
            .ToList();

        // DB from previous translations — skip already translated keys
        var dbTranslated = new HashSet<string>();
        // 名前 (地名・船名・武器・装備) のキー (TranslationDatabase.ReadNameKeys。大文字小文字を区別しない) と、行の source・japanese
        var nameKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rowOf = new Dictionary<string, (string? Source, string? Japanese)>(StringComparer.Ordinal);
        if (dbPath != null && File.Exists(dbPath))
        {
            try
            {
                using var db = new TranslationDatabase(dbPath);
                // gamedata_cache.db は翻訳 DB と同じフォルダ (RevertNamesToEnglish と同じ)
                var gamedataPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath)) ?? "", "gamedata_cache.db");
                try
                {
                    if (db.ReadNameKeys(gamedataPath) is { } nk)
                        foreach (var k in nk.Places.Concat(nk.Ships).Concat(nk.Items)) nameKeys.Add(k);
                }
                catch (Exception ex) { Console.WriteLine($"  名前のキーを読めませんでした (名前の行も AI に送ります): {ex.Message}"); }
                using (var sc = db.Connection.CreateCommand())
                {
                    sc.CommandText = "SELECT key, source, japanese FROM translations";
                    using var sr = sc.ExecuteReader();
                    while (sr.Read()) rowOf[sr.GetString(0)] = (sr.IsDBNull(1) ? null : sr.GetString(1), sr.IsDBNull(2) ? null : sr.GetString(2));
                }
                // AI が英語原文をそのまま返して保存された行 (source='ai' かつ japanese と english が前後の空白を除いて一致) は
                // 翻訳済みとして扱わず、翻訳対象に入れる (DB の行は書き換えない。訳が返れば通常どおり上書きされる)
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = "SELECT key, japanese, english, source FROM translations WHERE japanese IS NOT NULL AND japanese != ''";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var key = reader.GetString(0);
                    var ja = reader.GetString(1);
                    var en = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    var source = reader.IsDBNull(3) ? "" : reader.GetString(3);
                    if (source == "ai" && ja.Trim() == en.Trim()) continue;
                    // 公式 ini の「訳が無い」印が入った行は翻訳済みとして扱わない (DB の行は書き換えない)
                    if (TranslationDatabase.IsMissingTranslationMarker(ja)) continue;
                    dbTranslated.Add(key);
                }
            }
            catch { }
        }

        int count = 0;
        int nameSkipped = 0;
        var keepEnglish = new List<string>();
        using (var writer = new StreamWriter(outputPath, false, System.Text.Encoding.UTF8))
        foreach (var (key, enVal) in english)
        {
            if (forceEnglishRegex.Any(r => r.IsMatch(key)))
                continue;

            if (dbTranslated.Contains(key))
                continue;

            if (japanese.TryGetValue(key, out var jaVal) && !string.IsNullOrWhiteSpace(jaVal)
                && !TranslationDatabase.IsMissingTranslationMarker(jaVal))
                continue;

            if (string.IsNullOrWhiteSpace(enVal))
                continue;

            // 名前のキーは AI に送らず、英語の原文のままにする (SetToOriginalEnglish。戻すのは TranslationDatabase.IsRevertibleNameRow の行だけ)
            if (nameKeys.Contains(key))
            {
                if (rowOf.TryGetValue(key, out var row) && TranslationDatabase.IsRevertibleNameRow(row.Source, row.Japanese))
                    keepEnglish.Add(key);
                nameSkipped++;
                continue;
            }

            var entry = new TranslationEntry { Key = key, English = enVal };
            writer.WriteLine(JsonSerializer.Serialize(entry,
                new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            count++;
        }

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 未翻訳エントリ: {count}");

        if (nameSkipped > 0)
        {
            Console.WriteLine($"  名前のキーを AI に送らない: {nameSkipped:N0} 件 (原文のままにする {keepEnglish.Count:N0} 件)");
            if (keepEnglish.Count > 0)
            {
                try
                {
                    using var db = new TranslationDatabase(dbPath!);
                    db.SetToOriginalEnglish(keepEnglish);
                }
                catch (Exception ex) { Console.WriteLine($"  名前の行を原文のままにできませんでした: {ex.Message}"); }
            }
        }
    }
}
