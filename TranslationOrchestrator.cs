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

    public async Task RunAsync()
    {
        var entries = LoadEntries();
        var startIdx = _progress.Done;
        var remaining = entries.Skip(startIdx).ToList();
        var total = remaining.Count;

        Console.WriteLine($"Total: {entries.Count}, Done: {startIdx}, Remaining: {total}");
        Console.WriteLine($"Backends: {string.Join(", ", _backends.Select(b => b.Name))}");

        if (total == 0)
        {
            Console.WriteLine("Nothing to translate.");
            return;
        }

        // Build batches with round-robin backend assignment
        var batchQueue = new List<(TranslationBackend backend, List<TranslationEntry> batch, int index)>();
        int pos = 0;
        int batchIdx = 0;
        int backendIdx = 0;
        while (pos < total)
        {
            var backend = _backends[backendIdx % _backends.Count];
            var size = Math.Min(backend.BatchSize, total - pos);
            var batch = remaining.GetRange(pos, size);
            batchQueue.Add((backend, batch, batchIdx));
            pos += size;
            batchIdx++;
            backendIdx++;
        }

        Console.WriteLine($"Batches: {batchQueue.Count}");

        // Group batches by backend for parallel execution
        var semaphores = _backends.ToDictionary(b => b.Name, _ => new SemaphoreSlim(1, 1));

        using var outFile = new StreamWriter(_translatedPath, append: true, encoding: System.Text.Encoding.UTF8);

        var tasks = batchQueue.Select(item => Task.Run(async () =>
        {
            var (backend, batch, idx) = item;
            var sem = semaphores[backend.Name];
            await sem.WaitAsync();
            try
            {
                await ProcessBatch(backend, batch, idx, outFile, total);
            }
            finally
            {
                sem.Release();
            }
        }));

        await Task.WhenAll(tasks);

        Console.WriteLine($"\nDone! Translated: {_successTotal}, Fallback: {_fallbackTotal}");
    }

    private async Task ProcessBatch(TranslationBackend backend, List<TranslationEntry> batch, int batchIdx, StreamWriter outFile, int total)
    {
        Dictionary<string, string>? resultMap = null;

        for (int attempt = 0; attempt < _maxRetries; attempt++)
        {
            try
            {
                resultMap = await backend.TranslateAsync(batch);
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [{backend.Name}] ERR attempt {attempt + 1}: {ex.Message}");
                if (attempt < _maxRetries - 1)
                    await Task.Delay(backend is ClaudeBackend or GeminiBackend ? 5000 : 3000);
            }
        }

        var lines = new List<string>();
        int ok = 0, fail = 0;

        foreach (var entry in batch)
        {
            string ja;
            if (resultMap != null && resultMap.TryGetValue(entry.Key, out var translated) && !string.IsNullOrWhiteSpace(translated))
            {
                ja = translated;
                ok++;
            }
            else
            {
                ja = entry.English;
                fail++;
            }
            lines.Add(JsonSerializer.Serialize(
                new { key = entry.Key, ja },
                new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }

        lock (_writeLock)
        {
            foreach (var line in lines)
                outFile.WriteLine(line);
            outFile.Flush();

            _successTotal += ok;
            _fallbackTotal += fail;
            _progress.Update(batch.Count);

            if (batchIdx % 10 == 0)
            {
                var pct = (double)_progress.Done / (total + _progress.Done - batch.Count) * 100;
                Console.WriteLine($"[{backend.Name}] {_progress.Done}/{_progress.Done + total - _progress.Done} ({pct:F1}%) OK:{_successTotal} FAIL:{_fallbackTotal}");
            }
        }
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

    public static void BuildUntranslatedList(
        Dictionary<string, string> english,
        Dictionary<string, string> japanese,
        string outputPath,
        List<string> forceEnglishPatterns)
    {
        var forceEnglishRegex = forceEnglishPatterns
            .Select(p => new System.Text.RegularExpressions.Regex(p))
            .ToList();

        using var writer = new StreamWriter(outputPath, false, System.Text.Encoding.UTF8);
        int count = 0;

        foreach (var (key, enVal) in english)
        {
            // Skip keys forced to English
            if (forceEnglishRegex.Any(r => r.IsMatch(key)))
                continue;

            // Skip if Japanese exists
            if (japanese.TryGetValue(key, out var jaVal) && !string.IsNullOrWhiteSpace(jaVal))
                continue;

            // Skip empty values
            if (string.IsNullOrWhiteSpace(enVal))
                continue;

            var entry = new TranslationEntry { Key = key, English = enVal };
            writer.WriteLine(JsonSerializer.Serialize(entry,
                new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            count++;
        }

        Console.WriteLine($"Untranslated entries: {count}");
    }
}
