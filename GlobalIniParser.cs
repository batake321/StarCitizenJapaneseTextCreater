using System.Text;

namespace StarCitizenJapaneseTextCreater;

public static class GlobalIniParser
{
    public static Dictionary<string, string> Parse(string filePath)
    {
        var entries = new Dictionary<string, string>();
        foreach (var line in File.ReadLines(filePath, Encoding.UTF8))
        {
            var idx = line.IndexOf('=');
            if (idx > 0)
            {
                var key = line[..idx];
                var val = line[(idx + 1)..];
                entries[key] = val;
            }
        }
        return entries;
    }

    public static void Write(string filePath, Dictionary<string, string> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        using var writer = new StreamWriter(filePath, false, new UTF8Encoding(true));
        foreach (var key in entries.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            writer.WriteLine($"{key}={entries[key]}");
        }
    }
}
