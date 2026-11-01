using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StarCitizenJapaneseTextCreater;

// 日本語フォント (gfx) を置く・元に戻す (設定タブの「日本語フォント (gfx)」の欄。画面の側は MainWindow.FontGfx.cs)。
//   字が足りず □ になる漢字を出すため、ゲームの日本語フォント (Data.p4k の中の Data/Localization/japanese_(japan)/UI/fonts/Shared/fonts_en.gfx) を、
//   字がすべて入ったもの (Noto Sans JP) に置き換えたものを <GamePath>\data\Localization\japanese_(japan)\UI\fonts\Shared\fonts_en.gfx に置く。
//   置くものは GitHub の releases/latest の添付 fonts_en.gfx と、その大きさ・SHA-256・作る元にした Data.p4k の中のファイルの記録
//   fonts_en.gfx.json (release.ps1 が毎回載せる)。
//   元のフォントが変わっていないかは、Data.p4k の中央ディレクトリ (Ship3DP4kIndex) の大きさと、p4k に記録された CRC (CRC-32C) を
//   json の記録 (baseSize・baseCrc32。baseCrc32 も p4k に記録された CRC-32C の値) と比べて確かめる。
//   置いたら、置いたファイルの SHA-256 を印 (fonts_en.gfx.scjptc) に書く。置き先の SHA-256 が json か印の値と同じなら、このアプリが置いたものとみなす。
//   置き先にこのアプリが置いたもの以外のファイルがあれば、最初の 1 つだけ fonts_en.gfx.bak に移しておき、元に戻すときに戻す
//   (.bak がもうあるときは fonts_en.gfx.bak.<yyyyMMddHHmmss> に移して残す。こちらは元に戻すでは戻さない)
internal static class FontGfxInstaller
{
    public const string GfxAssetName = "fonts_en.gfx";
    public const string InfoAssetName = "fonts_en.gfx.json";
    // gfx は日本語用 (書き出す言語がこれのときだけ置く)
    public const string Language = "japanese_(japan)";

    // fonts_en.gfx.json (release.ps1 が載せる。中身は AppData\font\fonts_en.gfx.json)
    public sealed class FontInfo
    {
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
        [JsonPropertyName("baseEntry")] public string BaseEntry { get; set; } = "";     // 作る元にした Data.p4k の中のファイル
        [JsonPropertyName("baseSize")] public long BaseSize { get; set; }
        [JsonPropertyName("baseCrc32")] public string BaseCrc32 { get; set; } = "";     // p4k に記録された CRC (CRC-32C)。16 進
        [JsonPropertyName("baseSha256")] public string BaseSha256 { get; set; } = "";
    }

    // Data.p4k の中の元のフォントが、このフォントを作ったときと同じか
    public enum BaseCheck { Same, Changed, Unknown }

    // HttpClient は AppUpdater と同じ作り方 (時間切れ 30 分、User-Agent はアプリの名前と版)。アプリの中で 1 つを使い回す
    private static readonly HttpClient Http = CreateHttp();
    // GitHub API (releases/latest) と json の取得の時間切れ (gfx 本体の取得は HttpClient の 30 分のまま)
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(30);

    private static HttpClient CreateHttp()
    {
        var v = typeof(FontGfxInstaller).Assembly.GetName().Version ?? new Version(0, 0, 0);
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"StarCitizenJapaneseTextCreater/{new Version(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build))}");
        return http;
    }

    public static string TargetPath(string gamePath) =>
        Path.Combine(gamePath, "data", "Localization", Language, "UI", "fonts", "Shared", GfxAssetName);
    public static string BackupPath(string target) => target + ".bak";
    public static string DownloadPath(string target) => target + ".download";
    // 置いたフォントを見分ける印 (置き先と同じフォルダの fonts_en.gfx.scjptc)。中身は置いたファイルの SHA-256 (小文字の 16 進) 1 行
    public static string MarkerPath(string target) => target + ".scjptc";

    // 印に書いた SHA-256 (無い・読めない・形が違うときは null)
    public static string? ReadMarker(string target)
    {
        try
        {
            var path = MarkerPath(target);
            if (!File.Exists(path)) return null;
            var s = File.ReadAllText(path).Trim().ToLowerInvariant();
            return s.Length == 64 && s.All(Uri.IsHexDigit) ? s : null;
        }
        catch { return null; }
    }

    // このアプリが置いたものか (SHA-256 が json の sha256 か印の値と同じ)
    public static bool IsOurs(string sha, FontInfo? info, string? marker) =>
        (info != null && sha == info.Sha256.ToLowerInvariant()) || (marker != null && sha == marker);

    // releases/latest の版と、添付の fonts_en.gfx.json・fonts_en.gfx の URL (無いものは null)
    public static async Task<(string Tag, string? InfoUrl, string? GfxUrl)> FindAssetsAsync()
    {
        string json;
        using (var cts = new CancellationTokenSource(ShortTimeout))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, AppUpdater.DefaultLatestUrl);
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var resp = await Http.SendAsync(req, cts.Token);
                if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"GitHub のリリースの情報を読めませんでした (HTTP {(int)resp.StatusCode})");
                json = await resp.Content.ReadAsStringAsync(cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                throw new TimeoutException($"GitHub のリリースの情報が {ShortTimeout.TotalSeconds:F0} 秒で取れませんでした");
            }
        }
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
        string? infoUrl = null, gfxUrl = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var a in assets.EnumerateArray())
            {
                if (!a.TryGetProperty("name", out var n) || !a.TryGetProperty("browser_download_url", out var u)) continue;
                var name = n.GetString();
                if (name == InfoAssetName) infoUrl = u.GetString();
                else if (name == GfxAssetName) gfxUrl = u.GetString();
            }
        return (tag, infoUrl, gfxUrl);
    }

    public static async Task<FontInfo> DownloadInfoAsync(string url)
    {
        string text;
        using (var cts = new CancellationTokenSource(ShortTimeout))
        {
            try
            {
                using var resp = await Http.GetAsync(url, cts.Token);
                resp.EnsureSuccessStatusCode();
                text = await resp.Content.ReadAsStringAsync(cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                throw new TimeoutException($"{InfoAssetName} が {ShortTimeout.TotalSeconds:F0} 秒で取れませんでした");
            }
        }
        var info = JsonSerializer.Deserialize<FontInfo>(text)
                   ?? throw new InvalidDataException($"{InfoAssetName} を読めません");
        if (info.Size <= 0 || info.Sha256.Length != 64) throw new InvalidDataException($"{InfoAssetName} の大きさ・SHA-256 が正しくありません");
        return info;
    }

    // gfx を tmp に取り、大きさと SHA-256 を json と比べる。違えば tmp を消して例外
    public static async Task DownloadGfxAsync(string url, string tmp, FontInfo info)
    {
        try
        {
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            await using var net = await resp.Content.ReadAsStreamAsync();
            await using var outFs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
            await net.CopyToAsync(outFs);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
        var size = new FileInfo(tmp).Length;
        var sha = await Task.Run(() => AppUpdater.Sha256OfFile(tmp));
        if (size != info.Size || sha != info.Sha256.ToLowerInvariant())
        {
            TryDelete(tmp);
            throw new InvalidDataException($"取ったフォントの中身が {InfoAssetName} と違います (大きさ {size:N0} / {info.Size:N0}、SHA-256 {sha} / {info.Sha256})");
        }
    }

    // Data.p4k の中の元のフォント (json の baseEntry) の大きさと p4k に記録された CRC (CRC-32C) を、json の baseSize・baseCrc32 と比べる。
    // Data.p4k は今の設定のゲームのフォルダのもの (Ship3DExports.CurrentStamp)。Reason は確認の文に出す理由
    public static async Task<(BaseCheck Kind, string Reason)> CheckBaseAsync(FontInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.BaseEntry) || !uint.TryParse(info.BaseCrc32, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var crc))
            return (BaseCheck.Unknown, $"{InfoAssetName} に元のフォントの記録が無いため、元のフォントが変わっていないか確かめられません");
        var stamp = Ship3DExports.CurrentStamp();
        if (stamp == null) return (BaseCheck.Unknown, "ゲームの Data.p4k が見つからないため、元のフォントが変わっていないか確かめられません");
        Dictionary<ulong, (long Size, uint Crc)> index;
        try { index = await Ship3DP4kIndex.GetAsync(stamp); }
        catch (Exception ex) { return (BaseCheck.Unknown, $"ゲームの Data.p4k を読めないため、元のフォントが変わっていないか確かめられません ({ex.Message})"); }
        if (!index.TryGetValue(Ship3DEstimator.Hash(Ship3DEstimator.Normalize(info.BaseEntry)), out var now))
            return (BaseCheck.Changed, $"Data.p4k に元のフォント ({info.BaseEntry}) がありません");
        if (now.Size != info.BaseSize || now.Crc != crc)
            return (BaseCheck.Changed, $"元のフォント ({info.BaseEntry}) の大きさ {now.Size:N0} バイト・p4k に記録された CRC (CRC-32C) {now.Crc:x8} (このフォントを作ったときは {info.BaseSize:N0} バイト・{crc:x8})");
        return (BaseCheck.Same, "");
    }

    // 置き先のフォルダを作る。作ったいちばん上のフォルダを返す (元から全部あれば null)
    public static string? CreateFolder(string target)
    {
        var dir = Path.GetDirectoryName(target)!;
        string? top = null;
        for (var d = dir; d != null && !Directory.Exists(d); d = Path.GetDirectoryName(d)) top = d;
        Directory.CreateDirectory(dir);
        return top;
    }

    // 途中でやめたとき、CreateFolder で作ったフォルダを空なら消す (下から順に、作ったいちばん上まで)
    public static void RemoveCreatedFolders(string target, string? top)
    {
        if (top == null) return;
        for (var d = Path.GetDirectoryName(target); d != null; d = Path.GetDirectoryName(d))
        {
            try
            {
                if (!Directory.Exists(d) || Directory.EnumerateFileSystemEntries(d).Any()) return;
                Directory.Delete(d);
            }
            catch { return; }
            if (string.Equals(d, top, StringComparison.OrdinalIgnoreCase)) return;
        }
    }

    // 取って確かめた tmp を置き先に置き、印を新しい SHA-256 で書き直す。置き先にこのアプリが置いたもの (SHA-256 が json か印の値と同じ) 以外の
    // ファイルがあれば、控え (.bak) が無いときは控えに移す (控えは最初の 1 つだけ)。控えがもうあるときは fonts_en.gfx.bak.<yyyyMMddHHmmss> に移して残す
    // 戻り値: Note はログに出す控えのこと (無ければ "")。MarkerError は印を書けなかった理由 (書けたら null。フォントは置けているので成功として扱う)
    public static (string Note, string? MarkerError) Place(string tmp, string target, FontInfo info)
    {
        var bak = BackupPath(target);
        var note = "";
        if (File.Exists(target) && !IsOurs(AppUpdater.Sha256OfFile(target), info, ReadMarker(target)))
        {
            if (!File.Exists(bak))
            {
                File.Move(target, bak);
                note = $"前からあったファイルを {Path.GetFileName(bak)} に移しました";
            }
            else
            {
                // 控え (.bak) はもうあるので上書きせず、日時を付けた名前に移して残す (元に戻すで戻すのは .bak だけ)
                var dated = DatedBackupPath(target);
                File.Move(target, dated);
                note = $"前からあったファイルを、控え ({Path.GetFileName(bak)}) がもうあるので {Path.GetFileName(dated)} に移しました";
            }
        }
        File.Move(tmp, target, overwrite: true);
        string? markerError = null;
        try { File.WriteAllText(MarkerPath(target), info.Sha256.ToLowerInvariant() + Environment.NewLine); }
        catch (Exception ex) { markerError = ex.Message; }
        return (note, markerError);
    }

    // fonts_en.gfx.bak.<yyyyMMddHHmmss> (同じ名前がもうあれば、後ろに -2, -3 … を付ける)
    private static string DatedBackupPath(string target)
    {
        var basePath = $"{BackupPath(target)}.{DateTime.Now:yyyyMMddHHmmss}";
        var path = basePath;
        for (var i = 2; File.Exists(path); i++) path = $"{basePath}-{i}";
        return path;
    }

    // 置き先と印を消し、控え (.bak) があれば置き先へ戻す (移す)。控えを戻したら true
    public static bool Remove(string target)
    {
        if (File.Exists(target)) File.Delete(target);
        if (File.Exists(MarkerPath(target))) File.Delete(MarkerPath(target));
        var bak = BackupPath(target);
        if (!File.Exists(bak)) return false;
        File.Move(bak, target);
        return true;
    }

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
