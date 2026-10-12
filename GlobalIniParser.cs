using System.Text;

namespace StarCitizenJapaneseTextCreater;

public static class GlobalIniParser
{
    // 行の区切りは LF (と CR LF) だけ。値の途中の単独の CR は値の一部として読む
    // (英語の global.ini に「CR\n」と書かれた値がある。例 item_Desc_rrs_combat_heavy_backpack_01_04_01。
    //  File.ReadLines は CR だけでも行を切るため、その値が CR の前で切れていた)
    public static Dictionary<string, string> Parse(string filePath)
    {
        var entries = new Dictionary<string, string>();
        foreach (var line in ReadLinesLf(filePath))
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

    // ファイルを LF で行に分ける (行末の CR LF の CR は除く。単独の CR は行に残す)。BOM は UTF-8 の読み込みで除かれる
    private static IEnumerable<string> ReadLinesLf(string filePath)
    {
        using var reader = new StreamReader(filePath, Encoding.UTF8);
        var sb = new StringBuilder();
        var buf = new char[65536];
        int n;
        while ((n = reader.Read(buf, 0, buf.Length)) > 0)
        {
            for (var i = 0; i < n; i++)
            {
                if (buf[i] != '\n') { sb.Append(buf[i]); continue; }
                if (sb.Length > 0 && sb[^1] == '\r') sb.Length--;
                yield return sb.ToString();
                sb.Clear();
            }
        }
        if (sb.Length > 0)
        {
            if (sb[^1] == '\r') sb.Length--;
            yield return sb.ToString();
        }
    }

    public static void Write(string filePath, Dictionary<string, string> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        using var writer = new StreamWriter(filePath, false, new UTF8Encoding(true));
        foreach (var key in entries.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            writer.WriteLine($"{key}={EscapeLineBreaks(entries[key])}");
        }
    }

    // 値の中の生の改行を、英語の global.ini と同じ書き方 (「\n」の 2 文字) にする (生の改行のままだと行が割れ、「=」の無い行ができる)。
    //   CR LF → \n、LF → \n、CR → \n。ただし CR のすぐ後に「\n」の 2 文字が続くときは CR を消すだけにする
    //   (英語の global.ini に「CR\n」と書かれた値がある。例 item_Desc_rrs_combat_heavy_backpack_01_04_01。改行は「\n」の 1 つ)
    internal static string EscapeLineBreaks(string value)
    {
        if (value.IndexOfAny(['\r', '\n']) < 0) return value;
        var sb = new StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '\r')
            {
                if (i + 1 < value.Length && value[i + 1] == '\n') { sb.Append("\\n"); i++; }
                else if (i + 2 < value.Length && value[i + 1] == '\\' && value[i + 2] == 'n') { }
                else sb.Append("\\n");
            }
            else if (ch == '\n') sb.Append("\\n");
            else sb.Append(ch);
        }
        return sb.ToString();
    }
}
