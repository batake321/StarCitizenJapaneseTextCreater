using System.Text;
using System.Text.RegularExpressions;

namespace StarCitizenJapaneseTextCreater;

// StarBreaker v0.2.2 の p4k extract・p4k list・cryxml convert-all (cli/src/p4k.rs・cryxml.rs) の移植
internal static class P4kTools
{
    private static readonly UTF8Encoding Utf8 = new(false);

    // p4k extract --filter <glob> | --regex <re> [--convert cryxml]: 一致し、元の大きさが 0 でない項目を outDir の下に「\」を「/」にしたパスで書く。
    // convertCryxml なら CryXmlB の中身は XML の文字にして書く (読めなければ書かない)。書いた数と失敗の数を返す
    public static (int Written, int Errors) Extract(P4kArchive p4k, string outDir, string? filter, string? regex, bool convertCryxml = false, CancellationToken ct = default, Action<string>? onError = null)
    {
        var re = regex != null ? new Regex(regex, RegexOptions.CultureInvariant) : null;
        var list = new List<int>();
        for (int i = 0; i < p4k.Count; i++)
        {
            if (p4k.UncompressedSize(i) <= 0) continue;
            var name = p4k.Name(i);
            if (filter != null ? !GlobMatch.MatchesFilter(name, filter) : re != null && !re.IsMatch(name)) continue;
            list.Add(i);
        }
        list.Sort((a, b) => p4k.HeaderOffset(a).CompareTo(p4k.HeaderOffset(b)));
        int written = 0, errors = 0;
        foreach (var dir in list.Select(i => Path.GetDirectoryName(Path.Combine(outDir, p4k.Name(i).Replace('\\', '/')))).Distinct())
            if (dir != null) try { Directory.CreateDirectory(dir); } catch (IOException e) { onError?.Invoke($"create dir {dir}: {e.Message}"); }
        Parallel.ForEach(list, new ParallelOptions { CancellationToken = ct }, i =>
        {
            var name = p4k.Name(i);
            var outPath = Path.Combine(outDir, name.Replace('\\', '/'));
            byte[] data;
            try { data = p4k.Read(i); }
            catch (Exception e) when (e is InvalidDataException or IOException or ZstdSharp.ZstdException) { Interlocked.Increment(ref errors); onError?.Invoke($"Read {name}: {e.Message}"); return; }
            try
            {
                if (convertCryxml && CryXmlB.IsCryXmlB(data))
                {
                    CryXmlB x;
                    try { x = CryXmlB.Parse(data); }
                    catch (InvalidDataException e) { Interlocked.Increment(ref errors); onError?.Invoke($"Convert (cryxml→xml) {name}: {e.Message}"); return; }
                    File.WriteAllBytes(outPath, Utf8.GetBytes(x.ToXml()));
                }
                else File.WriteAllBytes(outPath, data);
                Interlocked.Increment(ref written);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Interlocked.Increment(ref errors); onError?.Invoke($"Write {name}: {e.Message}"); }
        });
        return (written, errors);
    }

    // アプリから: starbreaker の `p4k extract` を動かしていた所の置き換え。Data.p4k の目次は使い回す (P4kArchive.Shared)。
    // 戻り値は starbreaker と同じ考え: 開けない・正規表現が読めないときだけ Code 1 (1 つずつの読み書きの失敗は数えるだけで 0)
    public static Task<(int Code, string Tail)> RunExtractAsync(string p4kPath, string outDir, string? filter, string? regex, bool convertCryxml = false, CancellationToken ct = default) => Task.Run(() =>
    {
        try
        {
            var a = P4kArchive.Shared(p4kPath);
            var (w, e) = Extract(a, outDir, filter, regex, convertCryxml, ct);
            return (0, $"{w} files written, {e} errors");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            return (1, e.Message);
        }
    }, ct);

    // p4k list --filter <glob>: 「名前<TAB>元の大きさ」の行 (目次の順)
    public static IEnumerable<(string Name, long Size)> List(P4kArchive p4k, string? filter)
    {
        for (int i = 0; i < p4k.Count; i++)
        {
            var name = p4k.Name(i);
            if (GlobMatch.MatchesFilter(name, filter)) yield return (name, p4k.UncompressedSize(i));
        }
    }

    // cryxml convert-all -i <in> -o <out> [--filter "*"]: in の下のファイル (名前が glob に一致するもの) のうち CryXmlB のものだけを、XML の文字にして out の同じ相対パスに書く
    public static (int Converted, int Errors) CryXmlConvertAll(string inDir, string outDir, string filter = "*", Action<string>? onError = null)
    {
        var files = Directory.EnumerateFiles(inDir, "*", SearchOption.AllDirectories).Where(f => GlobMatch.IsMatch(filter, Path.GetFileName(f))).ToList();
        if (files.Count == 0) throw new FileNotFoundException($"no files found in {inDir}");
        int conv = 0, errors = 0;
        Parallel.ForEach(files, f =>
        {
            var rel = Path.GetRelativePath(inDir, f);
            var outPath = Path.Combine(outDir, rel);
            try
            {
                var dir = Path.GetDirectoryName(outPath);
                if (dir != null) Directory.CreateDirectory(dir);
                var data = File.ReadAllBytes(f);
                if (!CryXmlB.IsCryXmlB(data)) return;
                File.WriteAllBytes(outPath, Utf8.GetBytes(CryXmlB.Parse(data).ToXml()));
                Interlocked.Increment(ref conv);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { Interlocked.Increment(ref errors); onError?.Invoke($"Error converting {f}: {e.Message}"); }
        });
        return (conv, errors);
    }
}
