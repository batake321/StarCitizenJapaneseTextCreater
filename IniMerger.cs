using System.Text.Json;
using System.Text.RegularExpressions;

namespace StarCitizenJapaneseTextCreater;

public static class IniMerger
{
    public static Dictionary<string, string> Merge(
        Dictionary<string, string> english,
        Dictionary<string, string> japanese,
        string translatedJsonlPath,
        List<string> forceEnglishPatterns)
    {
        var forceRegex = forceEnglishPatterns.Select(p => new Regex(p)).ToList();

        // Load AI translations
        var aiTranslated = new Dictionary<string, string>();
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
                        aiTranslated[key] = ja;
                }
                catch { }
            }
            Console.WriteLine($"  AI translations loaded: {aiTranslated.Count}");
        }

        var merged = new Dictionary<string, string>();
        int enForced = 0, jaOfficial = 0, jaAi = 0, enFallback = 0;

        foreach (var (key, enVal) in english)
        {
            if (forceRegex.Any(r => r.IsMatch(key)))
            {
                merged[key] = enVal;
                enForced++;
            }
            else if (aiTranslated.TryGetValue(key, out var aiVal) && !string.IsNullOrWhiteSpace(aiVal))
            {
                merged[key] = aiVal;
                jaAi++;
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

        // Add Japanese-only keys
        foreach (var (key, val) in japanese)
        {
            if (!merged.ContainsKey(key))
                merged[key] = val;
        }

        Console.WriteLine($"  Merged: {merged.Count} entries");
        Console.WriteLine($"    English forced (ship/location): {enForced}");
        Console.WriteLine($"    AI translated: {jaAi}");
        Console.WriteLine($"    Official Japanese: {jaOfficial}");
        Console.WriteLine($"    English fallback: {enFallback}");

        return merged;
    }
}
