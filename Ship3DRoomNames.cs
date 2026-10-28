using System.IO;
using System.Text.Json;

namespace StarCitizenJapaneseTextCreater;

// 3D 表示の部屋 (区画) の名前の AI 訳 (利用者の要望: 新しく見つけた英語の名前は AI 翻訳に入れる)。
//   ビューア (viewer.html の roomLabel) の語の表で日本語にできなかった区画の名前だけが来る。
//   訳は作業フォルダの ship3d_room_names.json に覚え、2 回目からは AI に聞かない。
//   AI は、AI 設定で有効になっている最初の翻訳バックエンド (TranslationBackend.Create)。無ければ訳さない (ビューアはデータの名前のまま)。
//   AI が英語のまま返した・空・長すぎる (30 字超)・改行やタブを含む訳は使わず、空として覚える (データの名前のまま。次からも聞かない)
internal static class Ship3DRoomNames
{
    private static readonly object Lock = new();
    private static Dictionary<string, string>? _cache;
    private static readonly HashSet<string> Pending = new(StringComparer.Ordinal);
    private static bool _noBackendLogged;

    private static string CachePath => Path.Combine(DetailPanel.WorkDir(), "ship3d_room_names.json");

    private static Dictionary<string, string> Cache()
    {
        if (_cache != null) return _cache;
        try
        {
            _cache = File.Exists(CachePath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CachePath)) ?? new()
                : new();
        }
        catch (Exception ex)
        {
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[3D モデル] 部屋の名前の訳 ({CachePath}) を読めませんでした (空として扱います): {ex.Message}");
            _cache = new();
        }
        return _cache;
    }

    private static void Save()
    {
        try
        {
            string json;
            lock (Lock) json = JsonSerializer.Serialize(Cache(), new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            var tmp = CachePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, CachePath, overwrite: true);
        }
        catch (Exception ex)
        {
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[3D モデル] 部屋の名前の訳を保存できませんでした: {ex.Message}");
        }
    }

    // 区画の名前 → 日本語 (訳せたものだけ)。覚えている訳はそのまま、まだのものは AI に聞く (同じ名前を同時に二重には聞かない)
    public static async Task<Dictionary<string, string>> ResolveAsync(IReadOnlyCollection<string> names)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<string>();
        lock (Lock)
        {
            var cache = Cache();
            foreach (var n in names.Distinct(StringComparer.Ordinal))
            {
                if (cache.TryGetValue(n, out var ja)) { if (ja.Length > 0) result[n] = ja; }
                else if (Pending.Add(n)) missing.Add(n);
            }
        }
        if (missing.Count == 0) return result;
        try
        {
            var cfg = App.Config.Translation.Backends.FirstOrDefault(b => b.Enabled);
            if (cfg == null)
            {
                if (!_noBackendLogged)
                {
                    _noBackendLogged = true;
                    App.AppendStartupLog(App.Config.WorkingDirectory, "[3D モデル] 翻訳バックエンドが有効になっていないので、部屋の名前を AI で訳しません ([AI 設定] で設定できます)");
                }
                return result;
            }
            var backend = TranslationBackend.Create(cfg);
            // 区画の名前は語を _ でつないだもの (例 bay_a、misc_hull_c_ring_01)。空白にして渡す
            var batch = missing.Select((n, i) => new TranslationEntry { Key = $"ship3d_room_{i}", English = n.Replace('_', ' ').Trim() }).ToList();
            var map = await backend.TranslateAsync(batch);
            lock (Lock)
            {
                var cache = Cache();
                for (int i = 0; i < missing.Count; i++)
                {
                    var ja = map != null && map.TryGetValue(batch[i].Key, out var v) ? (v ?? "").Trim() : null;
                    if (ja == null) continue;   // 返ってこなかった名前は覚えない (次にまた聞く)
                    var usable = ja.Length is > 0 and <= 30 && !ja.Contains('\n') && !ja.Contains('\t')
                                 && !string.Equals(ja, batch[i].English, StringComparison.OrdinalIgnoreCase);
                    cache[missing[i]] = usable ? ja : "";
                    if (usable) result[missing[i]] = ja;
                }
            }
            Save();
            App.AppendStartupLog(App.Config.WorkingDirectory,
                $"[3D モデル] 部屋の名前を AI ({cfg.Name}) で訳しました: {string.Join("、", missing.Select(n => result.TryGetValue(n, out var j) ? $"{n} → {j}" : $"{n} → (そのまま)"))}");
        }
        catch (Exception ex)
        {
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[3D モデル] 部屋の名前を AI で訳せませんでした: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            lock (Lock) foreach (var n in missing) Pending.Remove(n);
        }
        return result;
    }
}
