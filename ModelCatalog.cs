using System.Net.Http;
using System.Text.Json;

namespace StarCitizenJapaneseTextCreater;

// 起動時に各プロバイダーから「使えるモデル一覧」を取得してメモリに保持する。
// ファイルには保存しない。取得できなかったプロバイダーは null (確認できず) のまま。
public static class ModelCatalog
{
    // 1 プロバイダーあたりのタイムアウト (ページ送りも含めた合計)
    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(10);

    // キー: "claude" / "gemini" / "openai" (BackendConfig.Type の小文字)
    private static volatile Dictionary<string, List<ModelItem>> _models = new();

    // 取得できたプロバイダーのモデル一覧。取得できていなければ null
    public static List<ModelItem>? GetModels(string type)
    {
        var key = (type ?? "").ToLowerInvariant();
        return _models.TryGetValue(key, out var list) ? list : null;
    }

    public class CheckResult
    {
        // ログ用の 1 行 (API キーが 1 つも無ければ null)
        public string? LogLine { get; set; }
        // 使えなくなっているモデル (チェックが付いているバックエンドのみ)。「名前: モデル」
        public List<string> Unavailable { get; } = new();
    }

    // API キーが設定されているプロバイダーだけ一覧を取得する (チェックの有無は問わない)
    public static async Task<CheckResult> RefreshAsync(List<BackendConfig> backends)
    {
        var providers = new (string Type, string Label, Func<HttpClient, string, CancellationToken, Task<List<ModelItem>>> Fetch)[]
        {
            ("claude", "Anthropic", FetchAnthropicAsync),
            ("gemini", "Google", FetchGoogleAsync),
            ("openai", "OpenAI", FetchOpenAiAsync),
        };

        var tasks = new List<(string Type, string Label, Task<(List<ModelItem>? List, string? Reason)> Task)>();
        foreach (var p in providers)
        {
            // 同じ種類のバックエンドが複数あるときは、最初に見つかった API キーを使う
            var key = backends
                .Where(b => b.Type.Equals(p.Type, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(b.ApiKey))
                .Select(b => b.ApiKey.Trim())
                .FirstOrDefault();
            if (key == null) continue;
            tasks.Add((p.Type, p.Label, FetchSafeAsync(p.Fetch, key)));
        }

        var result = new CheckResult();
        if (tasks.Count == 0) return result;

        await Task.WhenAll(tasks.Select(t => t.Task));

        var newModels = new Dictionary<string, List<ModelItem>>();
        var parts = new List<string>();
        foreach (var t in tasks)
        {
            var (list, reason) = t.Task.Result;
            if (list != null)
            {
                newModels[t.Type] = list;
                parts.Add($"{t.Label} {list.Count} 件");
            }
            else
            {
                parts.Add($"{t.Label} 確認できず ({reason})");
            }
        }
        _models = newModels;
        result.LogLine = "モデル確認: " + string.Join(" / ", parts);

        // チェックが付いているバックエンドの設定モデルが一覧に無いものだけ拾う (取得できなかったプロバイダーは対象外)
        foreach (var b in backends.Where(b => b.Enabled))
        {
            if (!newModels.TryGetValue(b.Type.ToLowerInvariant(), out var list)) continue;
            var model = b.Model?.Trim() ?? "";
            if (model.Length == 0) continue;
            if (!list.Any(m => IsSameModel(m.Id, model)))
                result.Unavailable.Add($"{b.Name}: {model}");
        }
        return result;
    }

    // 一覧の ID と設定値が同じモデルか。完全一致のほか、日付付きの版と別名の関係
    // (一覧の ID が「設定値 + "-"」で始まる、または設定値が「一覧の ID + "-"」で始まる) も一致とみなす。
    // 誤って「使えない」と出さない方を優先する
    internal static bool IsSameModel(string listedId, string configured) =>
        listedId.Equals(configured, StringComparison.OrdinalIgnoreCase)
        || listedId.StartsWith(configured + "-", StringComparison.OrdinalIgnoreCase)
        || configured.StartsWith(listedId + "-", StringComparison.OrdinalIgnoreCase);

    private static async Task<(List<ModelItem>? List, string? Reason)> FetchSafeAsync(
        Func<HttpClient, string, CancellationToken, Task<List<ModelItem>>> fetch, string apiKey)
    {
        try
        {
            using var cts = new CancellationTokenSource(ProviderTimeout);
            using var http = new HttpClient { Timeout = ProviderTimeout };
            return (await fetch(http, apiKey, cts.Token), null);
        }
        catch (Exception ex)
        {
            // 失敗したプロバイダーは「確認できず」。理由は短く (例外メッセージは URL に API キーを含み得るので出さない)
            return (null, FailureReason(ex));
        }
    }

    internal static string FailureReason(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: not null } h => $"HTTP {(int)h.StatusCode.Value}",
        OperationCanceledException => "タイムアウト",
        HttpRequestException => "接続失敗",
        JsonException or KeyNotFoundException or InvalidOperationException => "応答の形式が不明",
        _ => "エラー",
    };

    // === Anthropic: GET /v1/models (has_more が true なら after_id で続きを取る) ===

    private static async Task<List<ModelItem>> FetchAnthropicAsync(HttpClient http, string apiKey, CancellationToken ct)
    {
        var all = new List<ModelItem>();
        string? afterId = null;
        while (true)
        {
            var url = "https://api.anthropic.com/v1/models";
            if (afterId != null) url += "?after_id=" + Uri.EscapeDataString(afterId);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("x-api-key", apiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            using var resp = await http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            var (items, next) = ParseAnthropic(json);
            all.AddRange(items);
            if (next == null) break;
            afterId = next;
        }
        // API は新しいモデルから順に返すので、その順のまま使う
        return all;
    }

    // 戻り値の Next は続きがあるときの after_id (無ければ null)
    internal static (List<ModelItem> Items, string? Next) ParseAnthropic(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var items = new List<ModelItem>();
        foreach (var m in root.GetProperty("data").EnumerateArray())
        {
            var id = m.GetProperty("id").GetString() ?? "";
            if (id.Length == 0) continue;
            var display = m.TryGetProperty("display_name", out var dn) && dn.ValueKind == JsonValueKind.String
                ? dn.GetString() ?? id : id;
            items.Add(new ModelItem(id, display));
        }
        var hasMore = root.TryGetProperty("has_more", out var hm) && hm.ValueKind == JsonValueKind.True;
        var lastId = root.TryGetProperty("last_id", out var li) && li.ValueKind == JsonValueKind.String ? li.GetString() : null;
        return (items, hasMore && !string.IsNullOrEmpty(lastId) ? lastId : null);
    }

    // === Google: GET /v1beta/models?key= (nextPageToken があれば pageToken で続きを取る) ===

    private static async Task<List<ModelItem>> FetchGoogleAsync(HttpClient http, string apiKey, CancellationToken ct)
    {
        var all = new List<ModelItem>();
        string? pageToken = null;
        while (true)
        {
            var url = "https://generativelanguage.googleapis.com/v1beta/models?key=" + Uri.EscapeDataString(apiKey);
            if (pageToken != null) url += "&pageToken=" + Uri.EscapeDataString(pageToken);
            using var resp = await http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            var (items, next) = ParseGoogle(json);
            all.AddRange(items);
            if (next == null) break;
            pageToken = next;
        }
        // 新しいものが上に来るよう、モデル ID の降順 (数字は数値として比較) に並べる
        all.Sort((a, b) => NaturalCompare(b.Id, a.Id));
        return all;
    }

    // name の "models/" を外したものをモデル ID とする。generateContent を含むものだけ
    internal static (List<ModelItem> Items, string? Next) ParseGoogle(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var items = new List<ModelItem>();
        if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in models.EnumerateArray())
            {
                if (!m.TryGetProperty("supportedGenerationMethods", out var methods) || methods.ValueKind != JsonValueKind.Array)
                    continue;
                if (!methods.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "generateContent"))
                    continue;
                var name = m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var id = name.StartsWith("models/", StringComparison.Ordinal) ? name["models/".Length..] : name;
                if (id.Length == 0) continue;
                var display = m.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String
                    ? dn.GetString() ?? id : id;
                items.Add(new ModelItem(id, display));
            }
        }
        var next = root.TryGetProperty("nextPageToken", out var npt) && npt.ValueKind == JsonValueKind.String ? npt.GetString() : null;
        return (items, string.IsNullOrEmpty(next) ? null : next);
    }

    // === OpenAI: GET /v1/models ===

    // チャット用でないモデルを除くための ID の部分文字列 (大文字小文字は区別しない)
    private static readonly string[] OpenAiNonChatMarkers =
    {
        "embedding", "tts", "whisper", "dall-e", "moderation", "transcribe",
        "realtime", "audio", "gpt-image", "sora", "davinci", "babbage",
    };

    private static async Task<List<ModelItem>> FetchOpenAiAsync(HttpClient http, string apiKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
        req.Headers.Add("Authorization", "Bearer " + apiKey);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        return ParseOpenAi(json);
    }

    // 新しいものが上に来るよう created (作成日時) の降順。同じなら ID の降順
    internal static List<ModelItem> ParseOpenAi(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<(string Id, long Created)>();
        foreach (var m in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            var id = m.GetProperty("id").GetString() ?? "";
            if (id.Length == 0) continue;
            if (OpenAiNonChatMarkers.Any(k => id.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;
            var created = m.TryGetProperty("created", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt64(out var v) ? v : 0;
            list.Add((id, created));
        }
        return list
            .OrderByDescending(x => x.Created)
            .ThenByDescending(x => x.Id, Comparer<string>.Create(NaturalCompare))
            .Select(x => new ModelItem(x.Id, x.Id))
            .ToList();
    }

    // 数字の並びを数値として比較する ("gemini-3.10" > "gemini-3.8")
    internal static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var na = a[si..i].TrimStart('0');
                var nb = b[sj..j].TrimStart('0');
                if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                var c = string.CompareOrdinal(na, nb);
                if (c != 0) return c;
            }
            else
            {
                var c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}
