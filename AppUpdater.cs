using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StarCitizenJapaneseTextCreater;

// アプリの自動更新 (差分)。
//   確かめる (CheckAsync。ダウンロードはしない): GitHub の releases/latest (トークン無し。プレリリース・下書きは GitHub がここに出さない) を
//     ETag 付きで読み、tag_name (vX.Y.Z) が今の版 (アセンブリの Version の 3 桁) より新しければ、添付の update-manifest.json を読む。
//     manifest には publish の全ファイル (個人データ trade_cache.db* とインストーラー *Setup*.exe を除く) の相対パス・大きさ・SHA-256・
//     取りに行く URL (<SHA-256>.gz。同じ中身は前の版のリリースの添付の URL のまま)・.gz の大きさと、必要な .NET の版が入る (release.ps1 が作る)。
//     インストール先 (AppContext.BaseDirectory) のファイルと大きさ・SHA-256 を比べ、違う (無い) ファイルを差分とする。
//     間の版も含めた更新内容は releases の一覧から取る
//   取る (DownloadAsync。知らせの窓で［終了して更新］を選んだときだけ): .gz を取り、gzip を戻しながら SHA-256 と大きさを確かめて、
//     作業用のフォルダ (%LOCALAPPDATA%\StarCitizenJapaneseTextCreater\update\v<版>\files) に置く。
//     manifest に無いファイルは触らない (消さない)。作業フォルダ (D:\temp など) と個人データには触らない
//   入れ替え: 動いているアプリは自分を書き換えられないので、同じ exe の別の起動の口 (--apply-update <plan.json>) を、
//     インストール先のコード一式 (*.exe / *.dll / *.json / runtimes) を写した update\runner から起動する。runner はアプリが終わるのを待ち、
//     置き換えるファイルの控えを update\v<版>\backup に取ってから入れ替え、失敗したら控えから元に戻す。終わったらアプリを起動し直す。
//     結果は update\result.json と update\update_log.txt
//   入れ替えないとき (手で入れるよう知らせる): 必要な .NET の版が今の版より新しい / インストール先に書き込めない (Program Files など) /
//     開発中のフォルダ (publish・bin) から起動している / manifest が無い
public sealed class AppUpdater
{
    public const string DefaultLatestUrl = "https://api.github.com/repos/batake321/StarCitizenJapaneseTextCreater/releases/latest";
    public const string ManifestAssetName = "update-manifest.json";
    // 自動の確かめ (起動時・毎日の時刻) は、前の確かめからこれより短ければしない (手のボタンはいつでも)。トークン無しの回数制限 (1 時間に 60 回) への配慮
    public static readonly TimeSpan MinAutoInterval = TimeSpan.FromMinutes(30);

    // ===== manifest (release.ps1 が作る) =====
    public sealed class Manifest
    {
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("runtimes")] public List<RuntimeReq> Runtimes { get; set; } = new();
        [JsonPropertyName("files")] public List<ManifestFile> Files { get; set; } = new();
    }
    public sealed class RuntimeReq
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("version")] public string Version { get; set; } = "";
    }
    public sealed class ManifestFile
    {
        [JsonPropertyName("path")] public string Path { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("gzsize")] public long GzSize { get; set; }   // 取りに行く .gz の大きさ (知らせの窓のダウンロードの大きさ)
        // 同梱の大きな DB (DbDelta.DeltaFiles) の、隣り合う版どうしの差分 (前の版の manifest の分も引き継いで、全部の版の分を持つ)
        [JsonPropertyName("deltas")] public List<DeltaEntry> Deltas { get; set; } = new();
        // 取り方 (アプリが決める。manifest には無い): 差分を当てるときの順の差分 (空なら .gz を丸ごと取る)
        [JsonPropertyName("chain")] public List<DeltaEntry> Chain { get; set; } = new();
    }
    public sealed class DeltaEntry
    {
        [JsonPropertyName("fromVersion")] public string FromVersion { get; set; } = "";
        [JsonPropertyName("toVersion")] public string ToVersion { get; set; } = "";
        [JsonPropertyName("from")] public string FromSha256 { get; set; } = "";
        [JsonPropertyName("to")] public string ToSha256 { get; set; } = "";
        [JsonPropertyName("codec")] public string Codec { get; set; } = "";
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    }

    // ===== 入れ替えの計画 (アプリが書き、runner が読む) =====
    public sealed class ApplyPlan
    {
        public string Version { get; set; } = "";
        public string InstallDir { get; set; } = "";
        public string StagingDir { get; set; } = "";     // files の下に相対パスで置いた新しいファイル
        public string BackupDir { get; set; } = "";
        public List<ManifestFile> Files { get; set; } = new();
        public int WaitPid { get; set; }
        public string WaitProcessName { get; set; } = "";   // 待つアプリのプロセスの名前と開始時刻 (UTC の Ticks)。PID の使い回しを見分ける
        public long WaitProcessStartUtc { get; set; }
        public bool Restart { get; set; }
        public string RestartExe { get; set; } = "";
        public string WorkDir { get; set; } = "";            // plan を作ったときの作業フォルダ (runner は、これと今の設定の両方で DB を外すか決める)
    }

    public sealed class ApplyResult
    {
        public string Version { get; set; } = "";
        public bool Ok { get; set; }
        public string Message { get; set; } = "";
        public string FinishedAt { get; set; } = "";
        public bool Reported { get; set; }   // アプリがログ・知らせに出したら true
        public bool Manual { get; set; }     // 戻せず、手で直す必要がある (アプリの起動のときに窓で知らせる)
    }

    // ===== 覚えておくこと (update\state.json) =====
    public sealed class State
    {
        public string ETag { get; set; } = "";
        public string LatestJson { get; set; } = "";     // ETag で 304 のときに使う、前に読んだ releases/latest
        public DateTime LastCheckUtc { get; set; }
        public string LastDailyDate { get; set; } = "";  // 毎日の時刻の確かめをした日 (yyyy-MM-dd)
        public DateTime RateLimitResetUtc { get; set; }
        public string SkippedVersion { get; set; } = "";  // ［今は更新しない］を選んだ版 (自動の確かめでは、この版では知らせない)
    }

    // ===== 確かめの結果 =====
    public enum CheckKind { UpToDate, Available, ManualNeeded, Failed, Skipped }
    public sealed class CheckResult
    {
        public CheckKind Kind { get; set; }
        public string Version { get; set; } = "";
        public string Message { get; set; } = "";
        public string ReleaseNotes { get; set; } = "";    // 今の版より新しい版の更新内容 (Markdown。新しい順。読めなければ "")
        public bool NotesFailed { get; set; }
        public string ReleaseUrl { get; set; } = "";
        public string DbNote { get; set; } = "";   // 同梱の DB を入れ替えないときの知らせ (作業フォルダがインストール先と同じ)
        public ApplyPlan? Plan { get; set; }
        public long DownloadBytes { get; set; }   // 差分の .gz の合計 (manifest の gzsize)
        public int ChangedFiles { get; set; }
    }

    public string RootDir { get; }
    public string InstallDir { get; }
    public string LatestUrl { get; }
    public string ReleasesUrl { get; }
    public Version CurrentVersion { get; }
    private readonly HttpClient _http;
    private readonly Action<string> _log;

    // 作業フォルダ。試しのとき (workDir を渡したとき) 以外は、その時の App.Config.WorkingDirectory
    private readonly string? _workDirOverride;
    public string WorkDir => _workDirOverride ?? App.Config?.WorkingDirectory ?? "";

    // workDir: 作業フォルダ (既定は設定の作業フォルダ)。インストール先と同じなら同梱の DB を入れ替えない
    public AppUpdater(Action<string> log, string? rootDir = null, string? installDir = null, string? latestUrl = null, HttpClient? http = null,
                      Version? currentVersion = null, string? releasesUrl = null, string? workDir = null)
    {
        _log = log;
        _workDirOverride = workDir;
        RootDir = rootDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StarCitizenJapaneseTextCreater", "update");
        InstallDir = Path.GetFullPath(installDir ?? AppContext.BaseDirectory);
        LatestUrl = latestUrl ?? DefaultLatestUrl;
        ReleasesUrl = releasesUrl ?? (LatestUrl.EndsWith("/latest", StringComparison.Ordinal) ? LatestUrl[..^"/latest".Length] + "?per_page=30" : LatestUrl);
        CurrentVersion = currentVersion ?? Normalize(typeof(AppUpdater).Assembly.GetName().Version ?? new Version(0, 0, 0));
        if (http == null)
        {
            http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"StarCitizenJapaneseTextCreater/{CurrentVersion}");
        }
        _http = http;
        Directory.CreateDirectory(RootDir);
    }

    private static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    // "v2.1.2" / "2.1.2" → 2.1.2 (読めなければ null)。版の比べ方は Version (数の大小) で、4 つめの数は見ない。
    // 1.2.3-beta など - や + の付いたものはプレリリースとして扱わない (null)
    public static Version? ParseVersion(string? tag)
    {
        var s = (tag ?? "").Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        if (s.IndexOfAny(['-', '+']) >= 0) return null;
        return Version.TryParse(s, out var v) ? Normalize(v) : null;
    }

    // ===== 状態 =====
    private string StatePath => Path.Combine(RootDir, "state.json");
    public State LoadState()
    {
        try { return File.Exists(StatePath) ? JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) ?? new() : new(); }
        catch { return new(); }
    }
    public void SaveState(State s)
    {
        try { File.WriteAllText(StatePath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { _log($"更新の状態を保存できませんでした: {ex.Message}"); }
    }

    // 同梱の DB (と横のファイル)。作業フォルダがインストール先と同じときは、自動更新で入れ替えない (App.PlanBundledDbSync の「作業ディレクトリが
    // インストール先と同じ」と同じ判定。作業フォルダの DB そのものなので、入れ替えると作業中の中身が消える)
    public static readonly string[] BundledDbNames = ["translations.db", "gamedata_cache.db", "equipment_cache.db"];
    public bool WorkDirIsInstallDir => SameDbLocation(InstallDir, WorkDir);

    // 作業フォルダがインストール先と同じなら、plan から同梱の DB を外す (確かめ・ダウンロードを始めるとき・runner を起動するとき に判定し直す)。外した数
    public int ExcludeBundledDbsIfNeeded(ApplyPlan plan)
    {
        plan.WorkDir = WorkDir;
        if (!WorkDirIsInstallDir) return 0;
        var n0 = plan.Files.Count;
        plan.Files = plan.Files.Where(f => !IsBundledDbOrSide(f.Path)).ToList();
        if (plan.Files.Count != n0) _log($"作業フォルダがインストール先と同じなので、データベース {n0 - plan.Files.Count} ファイルは入れ替えません");
        return n0 - plan.Files.Count;
    }
    public static bool IsBundledDbOrSide(string relPath)
    {
        var parts = relPath.Replace('\\', '/').Split('/');
        if (parts.Length != 1) return false;   // インストール先のいちばん上にあるものだけ
        var name = parts[0];
        return BundledDbNames.Any(n => name.Equals(n, StringComparison.OrdinalIgnoreCase)
                                       || name.Equals(n + "-wal", StringComparison.OrdinalIgnoreCase)
                                       || name.Equals(n + "-shm", StringComparison.OrdinalIgnoreCase)
                                       || name.Equals(n + "-journal", StringComparison.OrdinalIgnoreCase));
    }

    // 入れ替えてよいフォルダか。だめなら理由 (null なら入れ替えてよい)
    public string? InstallDirProblem()
    {
        // 開発中のフォルダ (リポジトリの publish・bin の下) から起動しているときは入れ替えない (作業中の publish を書き換えないように)
        var parent = Path.GetDirectoryName(InstallDir.TrimEnd(Path.DirectorySeparatorChar));
        if (InstallDir.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || (parent != null && File.Exists(Path.Combine(parent, "StarCitizenJapaneseTextCreater.csproj"))))
            return "開発中のフォルダから起動しているため、自動では入れ替えません";
        try
        {
            var probe = Path.Combine(InstallDir, $".update_write_test_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            return $"インストール先 ({InstallDir}) に書き込めないため、自動では入れ替えません ({ex.Message})";
        }
        return null;
    }

    // ===== 確かめる (ダウンロードはしない) =====
    // auto: 自動の確かめ (前の確かめから MinAutoInterval 経っていなければしない。［今は更新しない］を選んだ版なら知らせない)
    public async Task<CheckResult> CheckAsync(bool auto, CancellationToken ct = default)
    {
        var state = LoadState();
        var now = DateTime.UtcNow;
        if (auto && now - state.LastCheckUtc < MinAutoInterval)
            return new CheckResult { Kind = CheckKind.Skipped, Message = "前の確かめから間が無いので確かめませんでした" };
        if (now < state.RateLimitResetUtc)
            return new CheckResult { Kind = CheckKind.Skipped, Message = $"GitHub の回数制限のため、{state.RateLimitResetUtc.ToLocalTime():HH:mm} まで確かめません" };
        state.LastCheckUtc = now;
        SaveState(state);
        try
        {
            // 1. releases/latest (ETag が同じなら 304 で、回数制限に数えられない)
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            if (!string.IsNullOrEmpty(state.ETag) && !string.IsNullOrEmpty(state.LatestJson)) req.Headers.TryAddWithoutValidation("If-None-Match", state.ETag);
            using var resp = await _http.SendAsync(req, ct);
            string json;
            if (resp.StatusCode == HttpStatusCode.NotModified) json = state.LatestJson;
            else if (resp.StatusCode is HttpStatusCode.Forbidden or (HttpStatusCode)429)
            {
                if (resp.Headers.TryGetValues("X-RateLimit-Reset", out var resets) && long.TryParse(resets.FirstOrDefault(), out var reset))
                {
                    state.RateLimitResetUtc = DateTimeOffset.FromUnixTimeSeconds(reset).UtcDateTime;
                    SaveState(state);
                    return Fail($"GitHub の回数制限に当たりました ({state.RateLimitResetUtc.ToLocalTime():HH:mm} まで待ちます)");
                }
                return Fail($"GitHub に断られました (HTTP {(int)resp.StatusCode})");
            }
            else if (resp.StatusCode == HttpStatusCode.NotFound) return new CheckResult { Kind = CheckKind.UpToDate, Message = "公開されたリリースがありません" };
            else
            {
                resp.EnsureSuccessStatusCode();
                json = await resp.Content.ReadAsStringAsync(ct);
                state.ETag = resp.Headers.ETag?.ToString() ?? "";
                state.LatestJson = json;
                SaveState(state);
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            var latest = ParseVersion(tag);
            if (latest == null) return Fail($"リリースの版 ({tag}) を読めませんでした");
            var notes = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "";
            var htmlUrl = root.TryGetProperty("html_url", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() ?? "" : "";
            var v3 = latest.ToString(3);
            if (latest <= CurrentVersion)
                return new CheckResult { Kind = CheckKind.UpToDate, Version = v3, Message = $"最新の版です (v{CurrentVersion.ToString(3)})" };
            if (auto && state.SkippedVersion == v3)
                return new CheckResult { Kind = CheckKind.Skipped, Version = v3, Message = $"v{v3} は［今は更新しない］を選んだ版なので知らせません" };

            var result = new CheckResult { Version = v3, ReleaseNotes = notes, ReleaseUrl = htmlUrl };
            // 間の版の更新内容も (新しい順に全部)。一覧を読めなければ最新の版の本文だけ、それも無ければ読めなかったとする
            var all = await ReadNotesBetweenAsync(latest, ct);
            if (all != null) result.ReleaseNotes = all;
            else if (string.IsNullOrWhiteSpace(notes)) result.NotesFailed = true;

            // 2. manifest
            string? manifestUrl = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                    if (a.TryGetProperty("name", out var n) && n.GetString() == ManifestAssetName && a.TryGetProperty("browser_download_url", out var u))
                        manifestUrl = u.GetString();
            if (manifestUrl == null)
                return Manual(result, $"新しい版 v{v3} には差分の更新の情報 ({ManifestAssetName}) が無いため、手で入れてください");
            var manifest = JsonSerializer.Deserialize<Manifest>(await GetStringAsync(manifestUrl, ct)) ?? throw new InvalidDataException("manifest を読めません");
            if (ParseVersion(manifest.Version) != latest) return Fail($"manifest の版 ({manifest.Version}) がリリースの版 ({tag}) と違います");

            // 3. 入れ替えられるか (.NET の版・書き込めるか)
            var running = Environment.Version;
            foreach (var r in manifest.Runtimes)
                if (Version.TryParse(r.Version, out var need) && need > running)
                    return Manual(result, $"新しい版 v{v3} には {r.Name} {r.Version} 以上が必要です (今は {running})。.NET を入れてから、手で入れてください");
            if (InstallDirProblem() is { } problem) return Manual(result, $"新しい版 v{v3} があります。{problem}");

            // 4. 差分 (インストール先のファイルと大きさ・SHA-256 を比べる。取るのは DownloadAsync)
            var vdir = Path.Combine(RootDir, $"v{v3}");
            var changed = new List<ManifestFile>();
            // 名前のおかしいパスが 1 つでもあれば、manifest を信用せず、この更新全体をやめる
            if (manifest.Files.FirstOrDefault(f => !SafeRelative(f.Path, InstallDir)) is { } bad) return Fail($"manifest のパスが正しくないため、この更新をやめました: {bad.Path}");
            var skipDbs = WorkDirIsInstallDir;
            if (skipDbs) result.DbNote = UpdateTexts.DbNotReplaced;
            foreach (var f in manifest.Files)
            {
                ct.ThrowIfCancellationRequested();
                if (skipDbs && IsBundledDbOrSide(f.Path)) continue;   // 差分の対象からも外し、取らない
                var local = Path.Combine(InstallDir, f.Path);
                string? localSha = File.Exists(local) ? Sha256OfFile(local) : null;
                if (localSha != null && new FileInfo(local).Length == f.Size && localSha == f.Sha256.ToLowerInvariant()) continue;
                // 同梱の大きな DB は、自分の DB の SHA-256 から隣り合う版どうしの差分をたどれれば、差分を古い順に当てる (丸ごとより小さいときだけ)
                f.Chain = localSha != null && DbDelta.IsDeltaFile(f.Path) ? FindChain(f, localSha) : new();
                if (f.Chain.Count > 0 && f.Chain.Sum(d => d.Size) >= f.GzSize) f.Chain = new();
                changed.Add(f);
            }
            result.Plan = new ApplyPlan
            {
                Version = v3, InstallDir = InstallDir, StagingDir = Path.Combine(vdir, "files"), BackupDir = Path.Combine(vdir, "backup"),
                Files = changed, RestartExe = Path.Combine(InstallDir, "StarCitizenJapaneseTextCreater.exe"),
            };
            result.Plan.WorkDir = WorkDir;
            result.Kind = CheckKind.Available;
            result.DownloadBytes = changed.Sum(DownloadSizeOf);
            result.ChangedFiles = changed.Count;
            result.Message = $"新しい版 v{v3} があります (差分 {changed.Count} ファイル、{result.DownloadBytes / 1024.0 / 1024.0:N1} MB)";
            _log(result.Message);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return Fail($"更新の確かめに失敗しました: {ex.Message}"); }

        CheckResult Fail(string m) { _log(m); return new CheckResult { Kind = CheckKind.Failed, Message = m }; }
        CheckResult Manual(CheckResult r, string m) { _log(m); r.Kind = CheckKind.ManualNeeded; r.Message = m; return r; }
    }

    // その DB の取り方の大きさ (差分をたどれれば差分の合計、無ければ .gz)
    private static long DownloadSizeOf(ManifestFile f) => f.Chain.Count > 0 ? f.Chain.Sum(d => d.Size) : f.GzSize;

    // 自分の DB の SHA-256 から、manifest の deltas を隣り合う版の順にたどって、目当ての SHA-256 まで行く差分の並び (行けなければ空)。
    // 知らない codec の差分は使わない
    internal static List<DeltaEntry> FindChain(ManifestFile f, string localSha)
    {
        var chain = new List<DeltaEntry>();
        var cur = localSha.ToLowerInvariant();
        var target = f.Sha256.ToLowerInvariant();
        for (int guard = 0; guard < 200 && cur != target; guard++)
        {
            var next = f.Deltas.FirstOrDefault(d => d.FromSha256.Equals(cur, StringComparison.OrdinalIgnoreCase) && DbDelta.Get(d.Codec) != null);
            if (next == null) return new();
            chain.Add(next);
            cur = next.ToSha256.ToLowerInvariant();
        }
        return cur == target ? chain : new();
    }

    // 今の版より新しく latest 以下の版の本文を、新しい順につないだ Markdown (releases の一覧から。プレリリース・下書きは除く)。読めなければ null
    private async Task<string?> ReadNotesBetweenAsync(Version latest, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ReleasesUrl);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) { _log($"更新内容 (リリースの一覧) を読めませんでした: HTTP {(int)resp.StatusCode}"); return null; }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var picked = new List<(Version V, string Tag, string Body)>();
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) continue;
                if (r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) continue;
                var tag = r.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                var v = ParseVersion(tag);
                if (v == null || v <= CurrentVersion || v > latest) continue;
                var body = r.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "";
                picked.Add((v, tag, body));
            }
            if (picked.Count == 0) return null;
            return string.Join("\n\n---\n\n", picked.OrderByDescending(x => x.V)
                .Select(x => string.IsNullOrWhiteSpace(x.Body) ? $"## {x.Tag}\n\n{UpdateTexts.NoNotesForVersion}" : x.Body.Trim()));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _log($"更新内容 (リリースの一覧) を読めませんでした: {ex.Message}"); return null; }
    }

    // ===== 取る (［終了して更新］を選んだときだけ) =====
    // <sha>.gz を取り、gzip を戻しながら確かめて plan.StagingDir に置く (前に取って確かめ済みのものは取り直さない)。
    // progress は (取った .gz の大きさ, 合計)。失敗したら例外 (インストール先には触っていないので、アプリはそのまま使える)
    public async Task DownloadAsync(ApplyPlan plan, IProgress<(long Done, long Total)>? progress, CancellationToken ct = default)
    {
        ExcludeBundledDbsIfNeeded(plan);
        long total = plan.Files.Sum(DownloadSizeOf), done = 0;
        progress?.Report((0, total));
        foreach (var f in plan.Files)
        {
            ct.ThrowIfCancellationRequested();
            var dest = Path.Combine(plan.StagingDir, f.Path);
            var before = done;
            if (!(File.Exists(dest) && new FileInfo(dest).Length == f.Size && Sha256OfFile(dest) == f.Sha256.ToLowerInvariant()))
            {
                var viaDelta = false;
                if (f.Chain.Count > 0)
                {
                    try
                    {
                        await ApplyChainAsync(f, Path.Combine(plan.InstallDir, f.Path), dest, ct, n => progress?.Report((before + n, total)));
                        viaDelta = true;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        // 差分が欠けている・当てた結果が合わないときは、その DB を丸ごと (圧縮) 取る
                        _log($"{f.Path} の差分を当てられなかったので、丸ごと取ります: {ex.Message}");
                        total += f.GzSize;
                    }
                }
                if (!viaDelta) await DownloadVerifiedAsync(f, dest, ct, n => progress?.Report((before + n, total)));
            }
            done += DownloadSizeOf(f);
            progress?.Report((done, total));
        }
        WritePlanDurable(Path.Combine(Path.GetDirectoryName(plan.StagingDir)!, "plan.json"), plan);
        _log($"新しい版 v{plan.Version} の差分 {plan.Files.Count} ファイル ({total / 1024.0 / 1024.0:N1} MB) を {plan.StagingDir} に用意しました");
    }

    // 差分を古い順に取って当てる。1 つ当てるたびに、その版の DB の SHA-256 と合うかを確かめる。最後に目当ての SHA-256 と大きさを確かめて dest に置く
    private async Task ApplyChainAsync(ManifestFile f, string localPath, string dest, CancellationToken ct, Action<long>? onBytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var work = dest + ".delta";
        Directory.CreateDirectory(work);
        try
        {
            var current = localPath;
            long got = 0;
            int step = 0;
            foreach (var d in f.Chain)
            {
                ct.ThrowIfCancellationRequested();
                var codec = DbDelta.Get(d.Codec) ?? throw new InvalidDataException($"差分の作り方 {d.Codec} を知りません");
                var patch = Path.Combine(work, $"{step}.patch");
                var before = got;
                using (var resp = await _http.GetAsync(d.Url, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    resp.EnsureSuccessStatusCode();
                    await using var net = await resp.Content.ReadAsStreamAsync(ct);
                    await using var counting = new CountingStream(net, n => onBytes?.Invoke(before + n));
                    await using var outFs = new FileStream(patch, FileMode.Create, FileAccess.Write, FileShare.None);
                    await counting.CopyToAsync(outFs, ct);
                }
                got += new FileInfo(patch).Length;
                if (Sha256OfFile(patch) != d.Sha256.ToLowerInvariant()) throw new InvalidDataException($"差分 v{d.FromVersion}→v{d.ToVersion} の中身が manifest と違います");
                var next = Path.Combine(work, $"{step}.db");
                await Task.Run(() => codec.Apply(current, patch, next), ct);
                if (Sha256OfFile(next) != d.ToSha256.ToLowerInvariant()) throw new InvalidDataException($"差分 v{d.FromVersion}→v{d.ToVersion} を当てた DB が v{d.ToVersion} の DB と合いません");
                if (current != localPath) File.Delete(current);
                File.Delete(patch);
                current = next;
                step++;
            }
            if (new FileInfo(current).Length != f.Size || Sha256OfFile(current) != f.Sha256.ToLowerInvariant())
                throw new InvalidDataException("差分を当てた DB が新しい版の DB と合いません");
            File.Move(current, dest, overwrite: true);
            _log($"{f.Path} を差分 {f.Chain.Count} 個 ({string.Join(" → ", f.Chain.Select(c => "v" + c.ToVersion))}) で更新しました");
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    // <sha>.gz を取り、gzip を戻しながら一時ファイルに書き、大きさと SHA-256 を確かめてから置く。取った (圧縮した) 大きさを返す
    private async Task<long> DownloadVerifiedAsync(ManifestFile f, string dest, CancellationToken ct, Action<long>? onBytes = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var tmp = dest + ".download";
        long compressed = 0;
        try
        {
            using var resp = await _http.GetAsync(f.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using var net = await resp.Content.ReadAsStreamAsync(ct);
            await using var counting = new CountingStream(net, n => { compressed = n; onBytes?.Invoke(n); });
            await using var gz = new GZipStream(counting, CompressionMode.Decompress);
            await using var outFs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
            await gz.CopyToAsync(outFs, ct);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
        var size = new FileInfo(tmp).Length;
        var sha = Sha256OfFile(tmp);
        if (size != f.Size || sha != f.Sha256.ToLowerInvariant())
        {
            try { File.Delete(tmp); } catch { }
            throw new InvalidDataException($"{f.Path} の中身が manifest と違います (大きさ {size} / {f.Size}、SHA-256 {sha} / {f.Sha256})");
        }
        File.Move(tmp, dest, overwrite: true);
        return compressed;
    }

    public static string Sha256OfFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    // 相対パスがインストール先の外に出ない・個人データ・インストーラーでないか
    private static readonly string[] NeverTouch = ["trade_cache.db", "trade_cache.db-wal", "trade_cache.db-shm", "trade_cache.db-journal"];
    // 受け付けないもの: 空・絶対パス・「.」「..」・各部分が「.」や空白で終わるもの・「:」を含むもの (::$DATA など)・「~」を含むもの (8.3 の短い名前)・
    // ファイル名に使えない文字・インストール先の外に出るもの・正規化 (Path.GetFullPath) したあとの名前が NeverTouch かインストーラーのもの
    public static bool SafeRelative(string rel, string? installDir = null)
    {
        if (string.IsNullOrWhiteSpace(rel) || Path.IsPathRooted(rel)) return false;
        var parts = rel.Replace('\\', '/').Split('/');
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var p in parts)
        {
            if (p is "" or "." or "..") return false;
            if (p.EndsWith('.') || p.EndsWith(' ') || p.StartsWith(' ')) return false;
            if (p.Contains(':') || p.Contains('~') || p.IndexOfAny(invalid) >= 0) return false;
        }
        string full, root;
        try
        {
            root = Path.GetFullPath(installDir ?? AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            full = Path.GetFullPath(Path.Combine(root, rel));
        }
        catch { return false; }
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        var name = Path.GetFileName(full);
        if (NeverTouch.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        if (name.Contains("Setup", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    // ===== runner を起動する (アプリが閉じるときに呼ぶ) =====
    // インストール先のコード一式を update\runner に写し、そこから --apply-update を起動する (インストール先の exe・dll を開いたままにしないため)
    public bool LaunchApply(ApplyPlan plan, bool restart)
    {
        try
        {
            // 前の入れ替えが片付いていない (途中の印がある) ときは、新しい入れ替えを始めない
            if (File.Exists(JournalPathOf(RootDir)))
            {
                _log("前の入れ替えの途中の印が残っているため、新しい入れ替えを始めません (アプリを起動し直すと、前の入れ替えを片付けます)");
                return false;
            }
            ExcludeBundledDbsIfNeeded(plan);
            using (var me = Process.GetCurrentProcess())
            {
                plan.WaitPid = me.Id;
                plan.WaitProcessName = me.ProcessName;
                plan.WaitProcessStartUtc = me.StartTime.ToUniversalTime().Ticks;
            }
            plan.Restart = restart;
            var planPath = Path.Combine(Path.GetDirectoryName(plan.StagingDir)!, "plan.json");
            WritePlanDurable(planPath, plan);
            var runner = Path.Combine(RootDir, "runner");
            if (Directory.Exists(runner)) Directory.Delete(runner, true);
            Directory.CreateDirectory(runner);
            foreach (var src in Directory.EnumerateFiles(InstallDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(InstallDir, src);
                var ext = Path.GetExtension(src).ToLowerInvariant();
                var inRuntimes = rel.StartsWith("runtimes" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                if (!(inRuntimes || ext is ".exe" or ".dll" or ".json")) continue;
                if (rel.Contains("Setup", StringComparison.OrdinalIgnoreCase) && ext == ".exe") continue;
                var dst = Path.Combine(runner, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, true);
            }
            var exe = Path.Combine(runner, "StarCitizenJapaneseTextCreater.exe");
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = runner };
            psi.ArgumentList.Add("--apply-update");
            psi.ArgumentList.Add(planPath);
            Process.Start(psi);
            _log($"新しい版 v{plan.Version} の入れ替えを始めました (アプリが閉じた後に入れ替えます{(restart ? "。終わったら起動し直します" : "")})");
            return true;
        }
        catch (Exception ex)
        {
            _log($"入れ替えを始められませんでした: {ex.Message}");
            return false;
        }
    }

    // 前回の入れ替えの結果 (まだ知らせていないもの)。知らせたら Reported を立てる
    public ApplyResult? TakeUnreportedResult()
    {
        var path = Path.Combine(RootDir, "result.json");
        try
        {
            if (!File.Exists(path)) return null;
            var r = JsonSerializer.Deserialize<ApplyResult>(File.ReadAllText(path));
            if (r == null || r.Reported) return null;
            r.Reported = true;
            File.WriteAllText(path, JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true }));
            return r;
        }
        catch { return null; }
    }

    // ===== runner の中身 (--apply-update <plan.json> / --resume-update <journal.json>) =====
    // 入れ替えの間は名前付きの Mutex (UpdatingMutexName) を持つ。runner は印 (journal) を読む前にまず Mutex を取り、取れなければ何もせずに終わる (印は残す)。
    // アプリの普通の起動は、これを見て終わるまで待つ (App.WaitForUpdateAndRecover)。
    // 入れ替えを始める前に「途中」の印 (update\journal.json。plan の場所・入れ先・控えの場所と、入れ替えを始めたファイル) を書き、1 ファイルごとに書き直す。
    // 入れ替える順番は、データ・ライブラリ (DB・依存の dll・その他) を先、本体 (.deps.json → .runtimeconfig.json → StarCitizenJapaneseTextCreater.dll → .exe) を最後。
    // 書き込みは、控え・新しい中身・印・plan のどれも、WriteThrough で書いて Flush(true) してから名前を入れ替える。
    // 印は、入れ替えを最後まで済ませたか、元に戻し終えたときだけ消す (例外・時間切れ・読めない・Mutex が取れないときは消さない)。RunOnce の値も、印を消すときだけ消す
    public const string UpdatingMutexName = @"Local\SCJPTextCreator_Updating";

    public sealed class Journal
    {
        public string PlanPath { get; set; } = "";
        public string Version { get; set; } = "";
        public string InstallDir { get; set; } = "";
        public string BackupDir { get; set; } = "";
        public List<JournalEntry> Entries { get; set; } = new();
        public int Resumes { get; set; }
    }
    public sealed class JournalEntry
    {
        public string Path { get; set; } = "";
        public bool HadOriginal { get; set; }   // 入れ替える前にファイルがあった (控えを取った)
    }

    public static string JournalPathOf(string rootDir) => Path.Combine(rootDir, "journal.json");
    // 戻せず手で直す必要があると決まった印 (journal.json.manual)。あれば、起動のときに runner を起動し直さない (起動のたびに runner が繰り返し動かないように)
    public static string ManualMarkPathOf(string rootDir) => JournalPathOf(rootDir) + ".manual";

    // 確かに書く: 一時ファイルに WriteThrough で書いて Flush(true) してから名前を入れ替える
    internal static void WriteTextDurable(string path, string text)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    internal static void WritePlanDurable(string path, ApplyPlan plan) =>
        WriteTextDurable(path, JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true }));

    private static void CopyDurable(string src, string dst)
    {
        using var inFs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20);
        using var outFs = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.WriteThrough);
        inFs.CopyTo(outFs, 1 << 20);
        outFs.Flush(true);
    }

    // 本体のファイルは最後 (.deps.json → .runtimeconfig.json → dll → exe の順)。ほかは 0
    public static int CoreRank(string relPath)
    {
        var name = Path.GetFileName(relPath.Replace('/', Path.DirectorySeparatorChar));
        return name.ToLowerInvariant() switch
        {
            "starcitizenjapanesetextcreater.deps.json" => 1,
            "starcitizenjapanesetextcreater.runtimeconfig.json" => 2,
            "starcitizenjapanesetextcreater.dll" => 3,
            "starcitizenjapanesetextcreater.exe" => 4,
            _ => 0,
        };
    }

    // runner が今の設定の作業フォルダを読む (%LOCALAPPDATA%\StarCitizenJapaneseTextCreater\appsettings.json の WorkingDirectory。読めなければ null)
    public static string? ReadConfiguredWorkDir()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StarCitizenJapaneseTextCreater", "appsettings.json");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Name.Equals("WorkingDirectory", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String) return p.Value.GetString();
        }
        catch { }
        return null;
    }

    public static bool SameDbLocation(string installDir, string? workDir)
    {
        if (string.IsNullOrWhiteSpace(workDir)) return false;
        try
        {
            return BundledDbNames.Any(n => string.Equals(Path.GetFullPath(Path.Combine(installDir, n)), Path.GetFullPath(Path.Combine(workDir, n)), StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    // 1 回の runner の起動の終わり方
    private enum RunEnd { Done, KeepJournal }

    // runner: アプリが終わるのを待ち (最大 5 分)、入れ替える。testHook は試しのときだけ (n 番目のファイルを入れる前に呼ぶ。例外を投げると失敗の戻しを、
    // プロセスを止めると途中で切れたときを試せる)。戻り値は終了コード (0 = 成功、1 = 失敗して元に戻した、2 = 何もしなかったか、印を残した)
    public static int RunApply(string planPath, Action<int>? testHook = null, TimeSpan? mutexTimeout = null) =>
        RunApplyCore(planPath, null, testHook, fromRunOnce: false, restartAfterResume: false, mutexTimeout ?? TimeSpan.FromMinutes(2));

    // runner: 途中で切れた入れ替えを続ける。fromRunOnce: ログオンのときに RunOnce から起動された (印が無ければ何もせずに終わる。済んだ後にアプリは起動し直さない)。
    // restart: アプリの起動のときに App.WaitForUpdateAndRecover から起動された (済んだらアプリを起動し直す)
    public static int ResumeApply(string journalPath, bool fromRunOnce = false, bool restart = false, TimeSpan? mutexTimeout = null) =>
        RunApplyCore(null, journalPath, null, fromRunOnce, restart, mutexTimeout ?? TimeSpan.FromMinutes(2));

    // ===== 本体の入れ替えの途中で止まったときの立て直し (HKCU の RunOnce) =====
    // 本体のファイル (CoreRank の 4 つ) を入れ替え始める直前に、HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce に
    // 「update\runner の exe --resume-update <journal> --runonce」を書き、印を消すときに消す。
    // 本体が組み合わないまま止まってアプリが起動できなくなっても、次のログオンで runner の写し (入れ替える前のインストール先から写した完全なもの) が続ける。
    // 管理者の権限は使わない (HKCU だけ)。書けなければログに出して、そのまま続ける
    public const string RunOnceValueName = "SCJPTextCreatorResumeUpdate";
    public static string RunOnceKeyPath { get; set; } = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";   // 試しのときだけ差し替える

    public static string RunOnceCommand(string rootDir) =>
        $"\"{Path.Combine(Path.GetFullPath(rootDir), "runner", "StarCitizenJapaneseTextCreater.exe")}\" --resume-update \"{JournalPathOf(Path.GetFullPath(rootDir))}\" --runonce";

    internal static bool SetRunOnce(string rootDir, Action<string> log)
    {
        try
        {
            var runner = Path.Combine(rootDir, "runner", "StarCitizenJapaneseTextCreater.exe");
            if (!File.Exists(runner)) { log("入れ替えのプログラムの写しが無いので、ログオンのときの立て直しの登録 (RunOnce) をしません"); return false; }
            var cmd = RunOnceCommand(rootDir);
            if (cmd.Length > 260) { log($"ログオンのときの立て直しの登録 (RunOnce) をしません (コマンドが長すぎます: {cmd.Length} 文字)"); return false; }
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunOnceKeyPath, writable: true);
            key.SetValue(RunOnceValueName, cmd, Microsoft.Win32.RegistryValueKind.String);
            return true;
        }
        catch (Exception ex)
        {
            log($"ログオンのときの立て直しの登録 (RunOnce) を書けませんでした (そのまま続けます): {ex.Message}");
            return false;
        }
    }

    internal static void ClearRunOnce(Action<string> log)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunOnceKeyPath, writable: true);
            if (key?.GetValue(RunOnceValueName) != null) key.DeleteValue(RunOnceValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) { log($"ログオンのときの立て直しの登録 (RunOnce) を消せませんでした: {ex.Message}"); }
    }

    private static int RunApplyCore(string? planPath, string? journalPath, Action<int>? testHook, bool fromRunOnce, bool restartAfterResume, TimeSpan mutexTimeout)
    {
        var rootDir = journalPath != null ? Path.GetDirectoryName(Path.GetFullPath(journalPath))! : Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(planPath!))!)!;
        var jpath = JournalPathOf(rootDir);
        var logPath = Path.Combine(rootDir, "update_log.txt");
        void Log(string m) { try { File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {m}\n"); } catch { } }

        // 1. まず Mutex を取る (印を読む前)。取れなければ何もせずに終わる (印は残す)
        using var updating = new Mutex(false, UpdatingMutexName);
        var owned = false;
        try { owned = updating.WaitOne(mutexTimeout); } catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            Log("ほかの入れ替えが動いているため、何もせずに終わります (途中の印は残します)");
            return 2;
        }
        ApplyPlan? plan = null;
        ApplyResult? result = null;
        var end = RunEnd.KeepJournal;
        var restart = false;
        try
        {
            if (journalPath == null)
            {
                // 新しい入れ替え: 前の入れ替えが片付いていない (印がある) ときは始めない
                if (File.Exists(jpath))
                {
                    Log("前の入れ替えの途中の印が残っているため、新しい入れ替えを始めません (アプリを起動し直すと、前の入れ替えを片付けます)");
                    return 2;
                }
                plan = JsonSerializer.Deserialize<ApplyPlan>(File.ReadAllText(planPath!)) ?? throw new InvalidDataException("plan を読めません");
                restart = plan.Restart;
                Log($"v{plan.Version} の入れ替えを始めます ({plan.Files.Count} ファイル、入れ先 {plan.InstallDir})");
                // ふつうの［終了して更新］の最初の入れ替えだけ、アプリが終わるのを待つ (PID が使い回されていないか、名前と開始時刻で同じアプリか確かめる)
                if (!WaitForAppExit(plan, TimeSpan.FromMinutes(5)))
                {
                    Log("アプリが 5 分たっても終わらないため、入れ替えをやめました (インストール先には触っていません)");
                    return 2;
                }
                // 作業フォルダがインストール先と同じなら DB を外す (plan を作ったときの作業フォルダと、今の設定の作業フォルダの両方で判定)
                if (SameDbLocation(plan.InstallDir, plan.WorkDir) || SameDbLocation(plan.InstallDir, ReadConfiguredWorkDir()))
                {
                    var n0 = plan.Files.Count;
                    plan.Files = plan.Files.Where(f => !IsBundledDbOrSide(f.Path)).ToList();
                    if (plan.Files.Count != n0) Log($"作業フォルダがインストール先と同じなので、データベース {n0 - plan.Files.Count} ファイルは入れ替えません");
                }
                // 前の控えを消してから取り直す (古い控えを使わない)
                if (Directory.Exists(plan.BackupDir)) Directory.Delete(plan.BackupDir, true);
                var journal = new Journal { PlanPath = Path.GetFullPath(planPath!), Version = plan.Version, InstallDir = plan.InstallDir, BackupDir = plan.BackupDir };
                (result, var finished) = ApplyWithJournal(plan, journal, rootDir, Log, testHook);
                end = finished ? RunEnd.Done : RunEnd.KeepJournal;
            }
            else
            {
                // 続き (アプリの起動のとき・RunOnce): 前のアプリの終わりは待たない (PID が使い回されて別のプロセスを待つため)
                restart = restartAfterResume && !fromRunOnce;
                if (!File.Exists(jpath))
                {
                    if (fromRunOnce) { ClearRunOnce(Log); }   // もう済んでいた
                    return 0;
                }
                Journal? journal = null;
                try { journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(jpath)); } catch (Exception ex) { Log($"途中の印を読めません: {ex.Message}"); }
                if (journal != null)
                {
                    try { plan = JsonSerializer.Deserialize<ApplyPlan>(File.ReadAllText(journal.PlanPath)); } catch (Exception ex) { Log($"plan を読めません: {ex.Message}"); }
                }
                else
                {
                    // 印そのものを読めない: 入れ替え中だった版の plan (update<版>\plan.json) を探す。控えのフォルダがある plan が 1 つだけのときに限って使う
                    plan = FindPlanForBrokenJournal(rootDir, Log);
                }
                if (journal != null && plan != null)
                {
                    journal.Resumes++;
                    Log($"v{plan.Version} の入れ替えが途中で切れていたので、続けます ({journal.Entries.Count} ファイルまで入れ替え済み、{journal.Resumes} 回目)");
                    // 続けるのは 2 回まで (3 回目は戻すだけ。続けると切れるのを繰り返すとき)
                    (result, var finished) = ApplyWithJournal(plan, journal, rootDir, Log, null, rollbackOnly: journal.Resumes > 2);
                    end = finished ? RunEnd.Done : RunEnd.KeepJournal;
                }
                else
                {
                    // 印か plan を読めない: 控えのフォルダの中身とインストール先を突き合わせて、戻せる分を戻す
                    (result, var finished) = RestoreFromBackupFolders(rootDir, journal, plan, Log);
                    end = finished ? RunEnd.Done : RunEnd.KeepJournal;
                }
            }
        }
        catch (Exception ex)
        {
            result = new ApplyResult { Version = plan?.Version ?? "", Ok = false, Message = $"入れ替えを始められませんでした (途中の印があれば残します): {ex.Message}" };
            Log(result.Message);
            end = File.Exists(jpath) ? RunEnd.KeepJournal : RunEnd.Done;
        }
        finally
        {
            if (result != null)
            {
                result.FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                try { WriteTextDurable(Path.Combine(rootDir, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })); } catch { }
            }
            if (end == RunEnd.Done && File.Exists(jpath))
            {
                // 最後まで済ませたか、元に戻し終えた: 印と RunOnce の値を消す
                try { File.Delete(jpath); try { File.Delete(ManualMarkPathOf(rootDir)); } catch { } ClearRunOnce(Log); }
                catch (Exception ex) { Log($"途中の印を消せませんでした: {ex.Message}"); }
            }
            else if (result is { Manual: true })
            {
                // 手で直す必要がある: 印と控えは残し、起動のたびに runner を起動し直さないよう印を付ける。RunOnce の値も消す (ログオンのたびに動かないように)
                try { WriteTextDurable(ManualMarkPathOf(rootDir), result.Message); } catch { }
                ClearRunOnce(Log);
            }
            try { updating.ReleaseMutex(); } catch { }
        }
        // 起動し直す (失敗して元に戻したときも、元の版で起動し直す)。RunOnce から (ログオンのとき) は、アプリを勝手に開かないよう起動し直さない
        if (restart && plan != null && File.Exists(plan.RestartExe))
        {
            try { Process.Start(new ProcessStartInfo(plan.RestartExe) { UseShellExecute = true, WorkingDirectory = plan.InstallDir }); }
            catch (Exception ex) { Log($"起動し直せませんでした: {ex.Message}"); }
        }
        return result == null ? 2 : result.Ok ? 0 : end == RunEnd.Done ? 1 : 2;
    }

    // アプリ (plan の WaitPid・WaitProcessName・WaitProcessStartUtc) が終わるのを待つ。同じ PID でも名前か開始時刻が違えば、もう終わったとみなす
    private static bool WaitForAppExit(ApplyPlan plan, TimeSpan timeout)
    {
        if (plan.WaitPid <= 0 || plan.WaitPid == Environment.ProcessId) return true;
        try
        {
            using var p = Process.GetProcessById(plan.WaitPid);
            if (!string.IsNullOrEmpty(plan.WaitProcessName) && !p.ProcessName.Equals(plan.WaitProcessName, StringComparison.OrdinalIgnoreCase)) return true;
            if (plan.WaitProcessStartUtc != 0 && Math.Abs(p.StartTime.ToUniversalTime().Ticks - plan.WaitProcessStartUtc) > TimeSpan.TicksPerSecond) return true;
            return p.WaitForExit(timeout);
        }
        catch (ArgumentException) { return true; }   // もう終わっている
        catch (InvalidOperationException) { return true; }
    }

    // 印を書きながら入れ替える (続きのときは、もう新しい中身になっているファイルは飛ばす)。失敗したら印の分を戻す。rollbackOnly なら戻すだけ。
    // 戻り値の Finished は、最後まで済ませたか、元に戻し終えたか (true なら印を消してよい)
    internal static (ApplyResult Result, bool Finished) ApplyWithJournal(ApplyPlan plan, Journal journal, string rootDir, Action<string> Log, Action<int>? testHook, bool rollbackOnly = false)
    {
        var jpath = JournalPathOf(rootDir);
        var result = new ApplyResult { Version = plan.Version };
        void Save() => WriteTextDurable(jpath, JsonSerializer.Serialize(journal, new JsonSerializerOptions { WriteIndented = true }));
        var runOnceSet = false;
        try
        {
            if (rollbackOnly) throw new InvalidOperationException("入れ替えが何度も途中で切れたため、続けずに元に戻します");
            Directory.CreateDirectory(plan.BackupDir);
            Save();
            int i = 0;
            foreach (var f in plan.Files.OrderBy(x => CoreRank(x.Path)).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
            {
                // 本体のファイルを入れ替え始める直前に、ログオンのときの立て直しを登録する
                if (!runOnceSet && CoreRank(f.Path) > 0) { SetRunOnce(rootDir, Log); runOnceSet = true; }
                testHook?.Invoke(i++);
                if (!SafeRelative(f.Path, plan.InstallDir)) throw new InvalidDataException($"パスが正しくありません: {f.Path}");
                var target = Path.Combine(plan.InstallDir, f.Path);
                var staged = Path.Combine(plan.StagingDir, f.Path);
                var backup = Path.Combine(plan.BackupDir, f.Path);
                var sha = f.Sha256.ToLowerInvariant();
                if (journal.Entries.Any(e => e.Path == f.Path) && File.Exists(target) && Sha256OfFile(target) == sha) continue;   // 続き: 入れ替え済み
                if (!File.Exists(staged) || Sha256OfFile(staged) != sha) throw new InvalidDataException($"用意したファイルが無いか壊れています: {f.Path}");
                var had = File.Exists(target);
                if (had && !File.Exists(backup))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    CopyDurable(target, backup + ".tmp");
                    File.Move(backup + ".tmp", backup, overwrite: true);
                }
                if (!journal.Entries.Any(e => e.Path == f.Path))
                {
                    journal.Entries.Add(new JournalEntry { Path = f.Path, HadOriginal = had || File.Exists(backup) });
                    Save();
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var tmp = target + ".update-new";
                CopyDurable(staged, tmp);
                MoveWithRetry(tmp, target);
                if (Sha256OfFile(target) != sha) throw new IOException($"入れ替えた {f.Path} の中身が違います");
            }
            result.Ok = true;
            result.Message = $"v{plan.Version} に入れ替えました ({plan.Files.Count} ファイル)";
            Log(result.Message);
            // 成功: 用意したファイルと控えを消す
            DeleteVersionDir(Path.GetDirectoryName(plan.StagingDir)!, Log);
            return (result, true);
        }
        catch (Exception ex)
        {
            Log($"入れ替えに失敗しました: {ex.Message}。入れ替えた分を元に戻します");
            var notRestored = new List<string>();
            foreach (var e in Enumerable.Reverse(journal.Entries))
            {
                var target = Path.Combine(plan.InstallDir, e.Path);
                var backup = Path.Combine(plan.BackupDir, e.Path);
                try
                {
                    try { File.Delete(target + ".update-new"); } catch { }
                    if (e.HadOriginal)
                    {
                        if (!File.Exists(backup)) throw new FileNotFoundException("控えがありません");
                        CopyDurable(backup, target + ".update-restore");
                        MoveWithRetry(target + ".update-restore", target);
                    }
                    else if (File.Exists(target)) File.Delete(target);
                }
                catch (Exception rex) { notRestored.Add($"{target} ({rex.Message}。控えは {backup})"); }
            }
            result.Ok = false;
            if (notRestored.Count == 0)
            {
                result.Message = $"v{plan.Version} への入れ替えに失敗したため、元に戻しました: {ex.Message}";
                Log(result.Message);
                // 元に戻し終えた: 成功のときと同じく、用意したファイルと控え (update<版>) を消す
                DeleteVersionDir(Path.GetDirectoryName(plan.StagingDir)!, Log);
                return (result, true);
            }
            result.Manual = true;
            result.Message = ManualFixMessage(plan.Version, plan.InstallDir, plan.BackupDir, notRestored, ex.Message);
            Log(result.Message);
            return (result, false);
        }
    }

    // 戻せないときに、手で直す方法 (控えの場所と、手で入れ直すこと)
    private static string ManualFixMessage(string version, string installDir, string backupDir, IEnumerable<string> notRestored, string cause) =>
        $"{(string.IsNullOrEmpty(version) ? "前回の" : $"v{version} への")}入れ替えに失敗し、元に戻せないファイルがあります: {string.Join(", ", notRestored)}。元の失敗: {cause}\n" +
        $"手で直すには: アプリを閉じて、控えのフォルダ ({backupDir}) の中のファイルを、同じ場所 (相対パス) のまま {(string.IsNullOrEmpty(installDir) ? "インストール先" : installDir)} に上書きしてください。" +
        "それでも起動しないときは、リリースのページからインストーラーをダウンロードして入れ直してください (作業フォルダのデータはそのまま残ります)。" +
        "直したら、update のフォルダの journal.json と journal.json.manual を消してください (消すまで、次の自動更新は始めません)";

    // 印か plan を読めないとき: 控えのフォルダ (update\v<版>\backup) の中身を、インストール先と突き合わせて戻す。インストール先は、読めた方 (印・plan) から取る。
    // 戻せたら Finished = true (印を消す)。戻せなければ印と控えを残し、手で直す方法を書く
    private static (ApplyResult Result, bool Finished) RestoreFromBackupFolders(string rootDir, Journal? journal, ApplyPlan? plan, Action<string> Log)
    {
        var installDir = plan?.InstallDir ?? journal?.InstallDir ?? "";
        var backupDir = plan?.BackupDir ?? journal?.BackupDir ?? "";
        var version = plan?.Version ?? journal?.Version ?? "";
        var result = new ApplyResult { Version = version, Ok = false };
        // 控えは、読めた方 (plan・印) に書いてある 1 つだけを使う。分からなければ (update の下の v* の控えを当て推量で使わずに) 手で直してもらう
        if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir) || string.IsNullOrEmpty(backupDir) || !Directory.Exists(backupDir))
        {
            result.Manual = true;
            result.Message = ManualFixMessage(version, installDir, !string.IsNullOrEmpty(backupDir) ? backupDir : Path.Combine(rootDir, "v<版>", "backup"),
                ["(途中の印か入れ替えの計画を読めず、インストール先か控えが分かりません)"], "途中の印を読めません");
            Log(result.Message);
            return (result, false);
        }
        var notRestored = new List<string>();
        int restored = 0;
        foreach (var src in Directory.EnumerateFiles(backupDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(backupDir, src);
            if (src.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || !SafeRelative(rel, installDir)) continue;
            var target = Path.Combine(installDir, rel);
            try
            {
                if (File.Exists(target) && Sha256OfFile(target) == Sha256OfFile(src)) continue;
                CopyDurable(src, target + ".update-restore");
                MoveWithRetry(target + ".update-restore", target);
                restored++;
            }
            catch (Exception ex) { notRestored.Add($"{target} ({ex.Message}。控えは {src})"); }
        }
        // plan が読めれば、元に無かった (控えの無い) 新しいファイルも消す
        if (plan != null)
            foreach (var f in plan.Files)
            {
                var target = Path.Combine(installDir, f.Path);
                if (!File.Exists(Path.Combine(plan.BackupDir, f.Path)) && File.Exists(target) && Sha256OfFile(target) == f.Sha256.ToLowerInvariant())
                {
                    try { File.Delete(target); } catch (Exception ex) { notRestored.Add($"{target} ({ex.Message})"); }
                }
            }
        if (notRestored.Count == 0)
        {
            result.Message = $"v{version} への入れ替えが途中で切れ、途中の印か計画を読めなかったため、控えから {restored} ファイルを元に戻しました";
            Log(result.Message);
            // 元に戻し終えた: その版のフォルダ (update<版>。控えの 1 つ上) を消す
            DeleteVersionDir(Path.GetDirectoryName(Path.GetFullPath(backupDir).TrimEnd(Path.DirectorySeparatorChar))!, Log);
            return (result, true);
        }
        result.Manual = true;
        result.Message = ManualFixMessage(version, installDir, backupDir, notRestored, "途中の印を読めません");
        Log(result.Message);
        return (result, false);
    }

    // 印を読めないとき: update*\plan.json のうち、読めて、控えのフォルダ (BackupDir) があるものが 1 つだけならそれを返す。0 か 2 つ以上なら null (手で直してもらう)
    private static ApplyPlan? FindPlanForBrokenJournal(string rootDir, Action<string> Log)
    {
        var found = new List<ApplyPlan>();
        if (Directory.Exists(rootDir))
            foreach (var d in Directory.GetDirectories(rootDir, "v*"))
            {
                var pp = Path.Combine(d, "plan.json");
                if (!File.Exists(pp)) continue;
                try
                {
                    var p = JsonSerializer.Deserialize<ApplyPlan>(File.ReadAllText(pp));
                    if (p != null && !string.IsNullOrEmpty(p.BackupDir) && Directory.Exists(p.BackupDir)) found.Add(p);
                }
                catch (Exception ex) { Log($"plan を読めません ({pp}): {ex.Message}"); }
            }
        if (found.Count == 1) { Log($"途中の印を読めないため、v{found[0].Version} の plan の控えから戻します"); return found[0]; }
        Log(found.Count == 0 ? "途中の印を読めず、控えのある plan も見つかりません" : $"途中の印を読めず、控えのある plan が {found.Count} つあってどれか決められません");
        return null;
    }

    // update<版> (用意したファイル・控え・plan) を消す
    private static void DeleteVersionDir(string vdir, Action<string> Log)
    {
        try { Directory.Delete(vdir, true); } catch (Exception ex) { Log($"用意したファイルを消せませんでした: {ex.Message}"); }
    }

    // ===== アプリの起動のとき (App.OnStartup。二重起動の確かめより前) =====
    // 入れ替え中か (runner が Mutex を持っている)。持っていなければ false
    public static bool IsUpdating()
    {
        using var m = new Mutex(false, UpdatingMutexName);
        try
        {
            if (!m.WaitOne(0)) return true;
        }
        catch (AbandonedMutexException) { }   // runner が切れた (印があれば下で続ける)
        try { m.ReleaseMutex(); } catch { }
        return false;
    }

    // 入れ替えが終わるのを待つ (上限 timeout。終われば true)
    public static bool WaitUpdateFinished(TimeSpan timeout)
    {
        using var m = new Mutex(false, UpdatingMutexName);
        bool got;
        try { got = m.WaitOne(timeout); } catch (AbandonedMutexException) { got = true; }
        if (got) { try { m.ReleaseMutex(); } catch { } }
        return got;
    }

    public enum ResumeStart { None, Launched, Busy }

    // 途中の印が残っていれば (runner が途中で切れた)、update\runner から --resume-update を起動する (Launched: アプリは終わり、runner の後に起動し直される)。
    // まず Mutex を取ってから印を見る。取れなければ (もう runner が動いている) 新しい runner は起動しない (Busy)。
    // runner が無いときは、このプロセスで続ける (本体のファイルは使用中で入れ替えられないことがあり、そのときは戻す)
    public static ResumeStart ResumeInterruptedIfAny(string? rootDir = null)
    {
        rootDir ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StarCitizenJapaneseTextCreater", "update");
        var jpath = JournalPathOf(rootDir);
        var logPath = Path.Combine(rootDir, "update_log.txt");
        void Log(string m) { try { File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {m}\n"); } catch { } }
        using var m = new Mutex(false, UpdatingMutexName);
        var owned = false;
        try { owned = m.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
        if (!owned) return ResumeStart.Busy;
        try
        {
            if (!File.Exists(jpath)) return ResumeStart.None;
            // 手で直す必要があると決まっている: runner を起動し直さない (知らせは result.json で出す)
            if (File.Exists(ManualMarkPathOf(rootDir))) return ResumeStart.None;
            var runner = Path.Combine(rootDir, "runner", "StarCitizenJapaneseTextCreater.exe");
            if (File.Exists(runner))
            {
                var psi = new ProcessStartInfo(runner) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(runner)! };
                psi.ArgumentList.Add("--resume-update");
                psi.ArgumentList.Add(jpath);
                psi.ArgumentList.Add("--restart");
                Process.Start(psi);   // runner は、この Mutex を離してから取る
                Log("前回の入れ替えが途中で切れていたので、続きを始めました (アプリは一度終わり、入れ替えの後に起動し直します)");
                return ResumeStart.Launched;
            }
        }
        catch (Exception ex)
        {
            Log($"途中で切れた入れ替えの続きを始められませんでした (途中の印は残します): {ex.Message}");
            return ResumeStart.None;
        }
        finally { try { m.ReleaseMutex(); } catch { } }
        // runner が無い: このプロセスで続ける (Mutex は RunApplyCore が取り直す)
        Log("前回の入れ替えが途中で切れていました。入れ替えのプログラムが無いので、このプロセスで続けます");
        RunApplyCore(null, jpath, null, fromRunOnce: false, restartAfterResume: false, TimeSpan.FromSeconds(10));
        return ResumeStart.None;
    }

    private static void MoveWithRetry(string src, string dst)
    {
        for (int i = 0; ; i++)
        {
            try { File.Move(src, dst, overwrite: true); return; }
            catch (IOException) when (i < 20) { Thread.Sleep(500); }
            catch (UnauthorizedAccessException) when (i < 20) { Thread.Sleep(500); }
        }
    }

    // 読んだ大きさを知らせる読み取り専用の流れ (ダウンロードの進み具合)
    private sealed class CountingStream(Stream inner, Action<long> onRead) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { var n = inner.Read(buffer, offset, count); _read += n; onRead(_read); return n; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { var n = await inner.ReadAsync(buffer, ct); _read += n; onRead(_read); return n; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    // ===== 閉じたときに入れ替える (［終了して更新］) =====
    // MainWindow が取り終えた計画を置き、閉じる処理 (RunExitAsync) を通してアプリを閉じる。App の Exit (チェックポイントの後) で runner を起動する
    public static (AppUpdater Updater, ApplyPlan Plan)? PendingOnExit { get; set; }

    public static void LaunchPendingOnExit()
    {
        if (PendingOnExit is not { } p) return;
        PendingOnExit = null;
        p.Updater.LaunchApply(p.Plan, restart: true);
    }
}
