using System.Text.Json;
using System.Text.RegularExpressions;

namespace StarCitizenJapaneseTextCreater;

public static class IniMerger
{
    public static Dictionary<string, string> Merge(
        Dictionary<string, string> english,
        Dictionary<string, string> japanese,
        string translatedJsonlPath,
        List<string> forceEnglishPatterns,
        string? dbPath = null,
        List<(string English, string Japanese)>? glossary = null)
    {
        var forceRegex = forceEnglishPatterns.Select(p => new Regex(p)).ToList();

        // Load translations: DB takes priority over JSONL
        var translations = new Dictionary<string, string>();

        // First load JSONL
        if (File.Exists(translatedJsonlPath))
        {
            foreach (var line in File.ReadLines(translatedJsonlPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var key = doc.RootElement.GetProperty("key").GetString() ?? "";
                    var ja = doc.RootElement.GetProperty("ja").GetString() ?? "";
                    if (key.Length > 0 && ja.Length > 0)
                        translations[key] = ja;
                }
                catch { }
            }
            Console.WriteLine($"  JSONL translations loaded: {translations.Count}");
        }

        // Then overlay DB (manual edits and CSV imports take priority)
        if (dbPath != null && File.Exists(dbPath))
        {
            using var db = new TranslationDatabase(dbPath);
            var dbTranslations = db.GetAllTranslations();
            foreach (var (key, ja) in dbTranslations)
                translations[key] = ja;
            Console.WriteLine($"  DB translations loaded: {dbTranslations.Count}");
        }

        var merged = new Dictionary<string, string>();
        int enForced = 0, jaTranslated = 0, jaOfficial = 0, enFallback = 0;

        foreach (var (key, enVal) in english)
        {
            if (forceRegex.Any(r => r.IsMatch(key)))
            {
                merged[key] = enVal;
                enForced++;
            }
            else if (translations.TryGetValue(key, out var trVal) && !string.IsNullOrWhiteSpace(trVal))
            {
                merged[key] = trVal;
                jaTranslated++;
            }
            else if (japanese.TryGetValue(key, out var jaVal) && !string.IsNullOrWhiteSpace(jaVal))
            {
                merged[key] = jaVal;
                jaOfficial++;
            }
            else
            {
                merged[key] = enVal;
                enFallback++;
            }
        }

        foreach (var (key, val) in japanese)
        {
            if (!merged.ContainsKey(key))
                merged[key] = val;
        }

        // Clean up "TRANSLATION NOT FOUND" errors from official Japanese file
        int cleaned = 0;
        var keysToFix = merged.Where(kv =>
            kv.Value.Contains("TRANSLATION NOT FOUND FOR LOCID:", StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key).ToList();
        foreach (var key in keysToFix)
        {
            if (key.Equals("blank_space", StringComparison.OrdinalIgnoreCase))
                merged[key] = " ";
            else if (english.TryGetValue(key, out var enFallbackVal))
                merged[key] = enFallbackVal;
            else
                merged[key] = " ";
            cleaned++;
        }

        // Apply glossary replacements to all merged values
        int glossaryFixed = 0;
        if (glossary != null && glossary.Count > 0)
        {
            var keys = merged.Keys.ToList();
            foreach (var key in keys)
            {
                var val = merged[key];
                var newVal = val;
                foreach (var (en, ja) in glossary)
                    newVal = newVal.Replace(en, ja, StringComparison.OrdinalIgnoreCase);
                if (newVal != val)
                {
                    merged[key] = newVal;
                    glossaryFixed++;
                }
            }
        }

        Console.WriteLine($"  Merged: {merged.Count} entries");
        Console.WriteLine($"    English forced (ship/location): {enForced}");
        Console.WriteLine($"    Translated (AI/manual/CSV): {jaTranslated}");
        Console.WriteLine($"    Official Japanese: {jaOfficial}");
        Console.WriteLine($"    English fallback: {enFallback}");
        if (cleaned > 0)
            Console.WriteLine($"    TRANSLATION NOT FOUND cleaned: {cleaned}");
        if (glossaryFixed > 0)
            Console.WriteLine($"    Glossary applied: {glossaryFixed}");

        return merged;
    }
}
