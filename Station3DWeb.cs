using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace StarCitizenJapaneseTextCreater;

// ステーションの 3D 表示をビューア (wwwroot\station3d\viewer.html) で開く。WebView2 の用意は船の 3D と同じ Ship3DWeb.InitAsync
// (利用者データの置き場 webview2_ship3d・仮想ホスト scjt-viewer.example・ほかへ移る/新しい窓/右クリックのメニューを止める・ビューアの失敗をログに残す) を使う。
//   GLB と scene.json は船のキャッシュと同じ仮想ホスト (Ship3DExports.CacheHost → Ship3DExports.Folder) の stations3d\<短い名前>\ から読む。
//   見出しの言葉 (ゲームの訳) はここで引いて、URL の # の後ろに JSON で渡す (ビューアはファイルを読むだけで訳を持たない):
//     着陸パッド: HUDDisplayName の鍵 (@ui_interactor_pad_01 など) の日本語、無ければ英語 (例「パッド01」。@landing_pad_NN は日本語の訳が無く「Pad 03」)
//     ハンガー: Maps_Amenities_Hangar<S|M|L|XL> の日本語、無ければ英語 (例「ハンガー（大）」)
//     外の貨物エレベーター: 「外部貨物エレベーター」(利用者の言葉「Freight Elevator 貨物エレベーター」に、ハンガーの中の貨物エレベーターと取り違えないよう
//       ゲームの訳の「外部」(ui_CIToggleExteriorLights「外部ライト」など) を付けたもの) と、区画の鍵 (@cargodeck_NN) の日本語、無ければ英語 (「Ext. Freight 01」)。
//       台と区画の対応が推測 (deckGuess) なら「（推測）」を付ける
internal static class Station3DWeb
{
    public const string ViewerPath = "station3d/viewer.html";
    public const string FreightWord = "外部貨物エレベーター";
    public const string GuessWord = "（推測）";

    // その場所の 3D をビューアで開く (キャッシュのフォルダは開くたびに割り当て直す。設定で置き場所を変えたときのため)。
    // stillWanted: 見出しの言葉を引いた後に、まだ開くか (その間に別の場所に移ったら false。開かずに false を返す)
    public static async Task<bool> ShowAsync(WebView2 web, string shortName, string title, Func<bool>? stillWanted = null)
    {
        var core = web.CoreWebView2 ?? throw new InvalidOperationException("WebView2 が用意できていません");
        var scenePath = Station3DExports.ScenePath(shortName);
        var labels = await Task.Run(() => Labels(scenePath));
        if (stillWanted != null && !stillWanted()) return false;
        var folder = Ship3DExports.Folder;
        try { core.ClearVirtualHostNameToFolderMapping(Ship3DExports.CacheHost); } catch { }
        core.SetVirtualHostNameToFolderMapping(Ship3DExports.CacheHost, folder, CoreWebView2HostResourceAccessKind.Allow);
        long ticks = 0;
        try { ticks = File.GetLastWriteTimeUtc(scenePath).Ticks; } catch { }
        // ?v= は書き出し直したときに前のファイルを使わないため (scene.json の更新日時。ビューアは GLB にも同じ v を付ける)
        var scene = $"https://{Ship3DExports.CacheHost}/stations3d/{Uri.EscapeDataString(shortName)}/{Station3DExports.SceneFile}?v={ticks}";
        var url = $"https://{Ship3DWeb.ViewerHost}/{ViewerPath}?scene={Uri.EscapeDataString(scene)}&title={Uri.EscapeDataString(title)}" +
                  $"#labels={Uri.EscapeDataString(JsonSerializer.Serialize(labels))}";
        core.Navigate(url);
        return true;
    }

    // 見出しの言葉: 鍵 → 文 (鍵は scene.json の pads の key、freight の deck、"hangar:<区分>"、"freight"、"guess")
    internal static Dictionary<string, string> Labels(string scenePath)
    {
        var res = new Dictionary<string, string>(StringComparer.Ordinal) { ["freight"] = FreightWord, ["guess"] = GuessWord };
        (Dictionary<string, string> En, Dictionary<string, string> Ja) ini;
        try { ini = LoadIni(); }
        catch (Exception ex)
        {
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D] global.ini を読めませんでした: {ex.Message}");
            ini = (new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase));
        }
        string? Text(string k) => ResolveLoc(k, ini.Ja) ?? ResolveLoc(k, ini.En);
        foreach (var size in new[] { "S", "M", "L", "XL" })
            if (Text("Maps_Amenities_Hangar" + size) is { } h) res["hangar:" + size] = h;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(scenePath));
            if (doc.RootElement.TryGetProperty("markers", out var mk))
            {
                void Keys(string arr, string prop)
                {
                    if (!mk.TryGetProperty(arr, out var a) || a.ValueKind != JsonValueKind.Array) return;
                    foreach (var e in a.EnumerateArray())
                        if (e.TryGetProperty(prop, out var k) && k.ValueKind == JsonValueKind.String && k.GetString() is { Length: > 0 } key && !res.ContainsKey(key))
                            res[key] = Text(key) ?? key.TrimStart('@');
                }
                Keys("pads", "key");
                Keys("freight", "deck");
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D] {scenePath} を読めませんでした: {ex.Message}");
        }
        return res;
    }

    // 表示をやめる (読み込んだ GLB の分のメモリを放す)
    public static void Blank(WebView2 web) => Ship3DWeb.Blank(web);

    // ゲームの文 (作業ディレクトリの english\global.ini と japanese_(japan)\global.ini。StationMapPanel.LoadIni と同じ場所・同じ読み方 (GlobalIniParser)。
    // ファイルの更新日時が同じ間は読んだものを使い回す)
    private static readonly object IniLock = new();
    private static (DateTime En, DateTime Ja, Dictionary<string, string> EnDict, Dictionary<string, string> JaDict)? _ini;

    private static (Dictionary<string, string> En, Dictionary<string, string> Ja) LoadIni()
    {
        var enPath = Path.Combine(DetailPanel.WorkDir(), "english", "global.ini");
        var jaPath = Path.Combine(DetailPanel.WorkDir(), "japanese_(japan)", "global.ini");
        var enTime = File.Exists(enPath) ? File.GetLastWriteTimeUtc(enPath) : DateTime.MinValue;
        var jaTime = File.Exists(jaPath) ? File.GetLastWriteTimeUtc(jaPath) : DateTime.MinValue;
        lock (IniLock)
        {
            if (_ini is { } c && c.En == enTime && c.Ja == jaTime) return (c.EnDict, c.JaDict);
        }
        var en = enTime != DateTime.MinValue ? new Dictionary<string, string>(GlobalIniParser.Parse(enPath), StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
        var ja = jaTime != DateTime.MinValue ? new Dictionary<string, string>(GlobalIniParser.Parse(jaPath), StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
        lock (IniLock) _ini = (enTime, jaTime, en, ja);
        return (en, ja);
    }

    // キーの文 (StationMapPanel.ResolveLoc と同じ引き方: そのまま / @ 付き / @ 無し、見つからなければ ",P" を付けたキー)。無ければ null
    private static string? ResolveLoc(string raw, Dictionary<string, string> dict)
    {
        static string? Lookup(string r, Dictionary<string, string> d)
        {
            if (d.TryGetValue(r, out var v1) && !string.IsNullOrEmpty(v1)) return v1;
            if (!r.StartsWith('@') && d.TryGetValue("@" + r, out var v2) && !string.IsNullOrEmpty(v2)) return v2;
            if (r.StartsWith('@') && d.TryGetValue(r[1..], out var v3) && !string.IsNullOrEmpty(v3)) return v3;
            return null;
        }
        var v = Lookup(raw, dict) ?? Lookup(raw + ",P", dict);
        return v?.Replace("\\n", " ");
    }
}
