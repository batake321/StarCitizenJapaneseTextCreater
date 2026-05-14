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
    private readonly ConcurrentBag<TranslationEntry> _failedEntries = new();

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

        var tasks = _backends.Select(backend => ProcessBackendQueue(backend, perBackend[backend.Name], outFile, ct));
        await Task.WhenAll(tasks);
    }

    private async Task ProcessBackendQueue(TranslationBackend backend, List<List<TranslationEntry>> batches, StreamWriter outFile, CancellationToken ct)
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
            bool success = await ProcessBatch(backend, batches[i], outFile);
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

    private async Task<bool> ProcessBatch(TranslationBackend backend, List<TranslationEntry> batch, StreamWriter outFile)
    {
        Dictionary<string, string>? resultMap = null;

        for (int attempt = 0; attempt < _maxRetries; attempt++)
        {
            try
            {
                resultMap = await backend.TranslateAsync(batch);
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

        var lines = new List<string>();
        var translatedItems = new List<(string Key, string Japanese, string Translator)>();
        int ok = 0, fail = 0;

        foreach (var entry in batch)
        {
            if (resultMap != null && resultMap.TryGetValue(entry.Key, out var translated) && !string.IsNullOrWhiteSpace(translated))
            {
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
            _progress.Update(batch.Count);

            var done = _progress.Done;
            var pct = (double)done / _totalEntries * 100;
            Console.WriteLine($"[{Now}] [{backend.Name}] {done:N0}/{_totalEntries:N0} ({pct:F1}%) OK:{ok} FAIL:{fail}");
            ProgressChanged?.Invoke(done, _totalEntries, _successTotal, _fallbackTotal);
        }

        if (translatedItems.Count > 0)
            BatchTranslated?.Invoke(translatedItems);

        return ok > 0;
    }

    private static string Now => DateTime.Now.ToString("HH:mm:ss");

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
        if (dbPath != null && File.Exists(dbPath))
        {
            try
            {
                using var db = new TranslationDatabase(dbPath);
                foreach (var (key, _) in db.GetAllTranslations())
                    dbTranslated.Add(key);
            }
            catch { }
        }

        using var writer = new StreamWriter(outputPath, false, System.Text.Encoding.UTF8);
        int count = 0;

        foreach (var (key, enVal) in english)
        {
            if (forceEnglishRegex.Any(r => r.IsMatch(key)))
                continue;

            if (dbTranslated.Contains(key))
                continue;

            if (japanese.TryGetValue(key, out var jaVal) && !string.IsNullOrWhiteSpace(jaVal))
                continue;

            if (string.IsNullOrWhiteSpace(enVal))
                continue;

            var entry = new TranslationEntry { Key = key, English = enVal };
            writer.WriteLine(JsonSerializer.Serialize(entry,
                new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            count++;
        }

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 未翻訳エントリ: {count}");
    }
}
