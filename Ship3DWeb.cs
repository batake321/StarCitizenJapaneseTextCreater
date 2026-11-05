using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace StarCitizenJapaneseTextCreater;

// 船の 3D 表示の WebView2 (［詳細］の欄の中と、別ウィンドウ Ship3DWindow で使う。詳細の表の WebView2 とは別)。
//   利用者データの置き場: 作業ディレクトリの webview2_ship3d (詳細の webview2_detail とは分ける)
//   仮想ホスト (SetVirtualHostNameToFolderMapping):
//     scjt-viewer.example → アプリの同梱ファイル (exe の横の wwwroot。three.js とビューア ship3d\viewer.html)
//     scjt-ship3d.example → キャッシュのフォルダ (Ship3DExports.Folder。GLB)。ビューアから fetch で読むので、別のホストからの読み込みを許す (Allow)
//   ビューアの失敗 (chrome.webview.postMessage の {type:'error'}) はアプリのログに残す
internal static class Ship3DWeb
{
    public const string ViewerHost = "scjt-viewer.example";

    // ビューアの右上の閉じるが押された (どの WebView2 か)。詳細の欄は［表に戻る］と同じ、別ウィンドウは閉じる
    public static event Action<WebView2>? CloseRequested;

    // WebView2 を用意する (一度だけ)。使えなければ例外
    public static async Task InitAsync(WebView2 web)
    {
        if (web.CoreWebView2 != null) return;
        var folder = Path.Combine(DetailPanel.WorkDir(), "webview2_ship3d");
        Directory.CreateDirectory(folder);
        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: folder);
        await web.EnsureCoreWebView2Async(env);
        var core = web.CoreWebView2!;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        // 右クリックのメニュー (戻る・最新の情報に更新など) は出さない (右ドラッグで枠の外に出てから離したときに出ていた)
        core.Settings.AreDefaultContextMenusEnabled = false;
        // 中身はビューアだけ。ほかへ移る・新しい窓を開くことはさせない。
        // CoreWebView2.Navigate で開くビューアも IsUserInitiated が真になることがあり、それで止めると何も出ないので、
        // ビューア (ViewerHost) と空のページ (Blank の NavigateToString) だけを通す
        core.NavigationStarting += (_, e) =>
        {
            var uri = e.Uri ?? "";
            if (uri.StartsWith($"https://{ViewerHost}/", StringComparison.OrdinalIgnoreCase)
                || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                || uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;
            e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.WebMessageReceived += async (_, e) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "close") { CloseRequested?.Invoke(web); return; }   // ビューアの右上の閉じる (2026-11-05、利用者の指示)
                if (type == "error" && doc.RootElement.TryGetProperty("message", out var m))
                {
                    // ステーションの 3D のビューア (wwwroot\station3d) は source に "station3d" を付けて送る。ログの頭をそれに合わせる
                    var station3d = doc.RootElement.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.String && src.GetString() == "station3d";
                    App.AppendStartupLog(App.Config.WorkingDirectory, $"{(station3d ? "[ステーションの 3D]" : "[3D モデル]")} 表示: {m.GetString()}");
                }
                else if (type is "paintInit" or "paintTable")
                {
                    // 塗装の切り替え・色付けの設定 (ビューアの頭の説明。2026-11-05)。この WebView2 に今出している船のものだけを扱う
                    if (!Views.TryGetValue(web, out var view) || !doc.RootElement.TryGetProperty("ship", out var sh) || sh.GetString() != view.Short) return;
                    if (type == "paintInit") await PaintInitAsync(web, view);
                    else await PaintTableAsync(web, view, doc.RootElement.TryGetProperty("id", out var pid) ? pid.GetString() ?? "" : "");
                }
                else if (type == "ladderInit")
                {
                    // 船内を歩くモードのはしご (ビューアの walk.js。2026-11-05): その船の船内のはしごの行 (gamedata_cache.db の ship_ladders。ShipLadders) を返す。
                    // 表が無い・行が無いときは空 (ビューアは形の名前と乗り降りの点からだけはしごを作る)
                    if (!Views.TryGetValue(web, out var view) || !doc.RootElement.TryGetProperty("ship", out var sh) || sh.GetString() != view.Short) return;
                    var dbPath = Path.Combine(App.Config.WorkingDirectory, "gamedata_cache.db");
                    var rows = await Task.Run(() => ShipLadders.ReadFor(dbPath, view.Short));
                    Post(web, view, new
                    {
                        type = "ladders", ship = view.Short,
                        items = rows.Select(r => new { container = r.Container, entity = r.Entity, b = new[] { r.Bx, r.By, r.Bz }, t = new[] { r.Tx, r.Ty, r.Tz }, f = new[] { r.Fx, r.Fy, r.Fz },
                            height = r.Height, approachBottom = r.ApproachBottom, approachTop = r.ApproachTop }).ToArray(),
                    });
                }
                else if (type == "roomNames" && doc.RootElement.TryGetProperty("names", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    // 語の表で日本語にできなかった部屋の名前を AI で訳して返す (Ship3DRoomNames)。50 個・80 字までに限る
                    var names = arr.EnumerateArray().Select(x => x.GetString() ?? "")
                        .Where(x => x.Length is > 0 and <= 80).Distinct(StringComparer.Ordinal).Take(50).ToList();
                    if (names.Count == 0) return;
                    var map = await Ship3DRoomNames.ResolveAsync(names);
                    if (map.Count > 0 && web.CoreWebView2 != null)
                        web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "roomNames", map }));
                }
            }
            catch (Exception ex)
            {
                App.AppendStartupLog(App.Config.WorkingDirectory, $"[3D モデル] ビューアからの知らせを扱えませんでした: {ex.Message}");
            }
        };
        web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x1b, 0x1e, 0x24);
        core.SetVirtualHostNameToFolderMapping(ViewerHost, Path.Combine(AppContext.BaseDirectory, "wwwroot"), CoreWebView2HostResourceAccessKind.DenyCors);
    }

    // その船の GLB をビューアで開く (キャッシュのフォルダは開くたびに割り当て直す。設定で置き場所を変えたときのため)。
    // glbPath: 置き場所の中の別のファイル (保存しない設定で直接読んだものは作業用のフォルダにある。Ship3DExports.GetViewPathAsync)。null なら <短い名前>.glb
    // isItem: 船でない品 (武器・装備など。Ship3DExports.IsItemRecord) なら true。ビューアは ?item=1 で［船外］［船内］のボタンと「船内の位置」の欄を出さない (2026-11-05、利用者の指示「船内船外はおかしい」)
    // ビューアには ship=<短い名前> も渡し、塗装の一覧・初めの塗装・色付けの設定をやり取りする (Views・PaintInitAsync。2026-11-05)
    // shipKey: どの所持船の 3D か (ship_loadouts のキー。分からなければ null)。初めの塗装をその所持船の塗装にする
    public static void Show(WebView2 web, string shortName, string title, string? glbPath = null, bool isItem = false, string? shipKey = null)
    {
        var core = web.CoreWebView2 ?? throw new InvalidOperationException("WebView2 が用意できていません");
        Views.AddOrUpdate(web, new ShownShip(shortName, glbPath ?? Ship3DExports.GlbPath(shortName), isItem, shipKey));
        core.Navigate(ViewerUrl(core, shortName, glbPath, title) + (isItem ? "&item=1" : "") + $"&ship={Uri.EscapeDataString(shortName)}");
    }

    // ===== 塗装の切り替え・色付けの設定 (2026-11-05、利用者の指示「右端に縦のリストでバリエーションを表示して切替で見れるようにしたい」・
    // 「"船・装備・武器の 3D は常に色付け" これは設定で切れるようにしてください。」) =====
    // WebView2 ごとの、今出している船 (短い名前・GLB のファイル・船でない品か)
    private sealed record ShownShip(string Short, string Glb, bool IsItem, string? ShipKey);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WebView2, ShownShip> Views = new();

    // 一覧と初めの塗装を返す。色付けを切っているとき (設定 Ship3DPalette が false) は一覧を出さず、色付けなし (IdNoPalette) を初めに当てる。
    // 船でない品は一覧を出さない (色付けを切っていれば色付けなしだけ)。一覧は「標準」(GLB そのまま)、既定のロードアウトに塗装の入った船なら「塗装なし」、付けられる塗装。
    // 初めの塗装は保存した塗装 (ShipPaints.SavedFor)。どの所持船か分かる (Show の shipKey。［所持船］の行から開いた［詳細］の表示からの 3D) ならその所持船の塗装、
    // 分からなければその船の行のうち最初のもの。無ければ標準。
    // standardId: 「標準」の姿に当てる材質の表。GLB が古い版の材質の決め方で書かれていれば標準の表 (ShipPaints.IdStandardTable)、今の版なら "" (GLB の材質のまま)
    private static async Task PaintInitAsync(WebView2 web, ShownShip view)
    {
        object[] items = [];
        string initial = "";
        var standardId = await Task.Run(() => ShipPaints.IsOldGlb(view.Glb)) ? ShipPaints.IdStandardTable : "";
        if (!App.Config.Ship3DPalette) initial = ShipPaints.IdNoPalette;
        else if (!view.IsItem)
        {
            var (list, def, saved, ng) = await Task.Run<(IReadOnlyList<ShipPaints.Paint>, string?, string?, Dictionary<string, string>)>(() =>
            {
                var l = ShipPaints.ListFor(view.Short);
                if (l.Count == 0) return (l, null, null, new());
                var d = ShipPaints.DefaultPaintOf(view.Short);
                // 前に「出せない」と分かった塗装 (一覧で押せない形にする)
                var bad = ShipPaints.UnusableIds(view.Short, view.Glb, l.Select(p => p.Record).Append(ShipPaints.IdNone));
                return (l, d, ShipPaints.SavedFor("EntityClassDefinition." + view.Short, view.ShipKey), bad);
            });
            if (list.Count > 0)
            {
                var all = new List<object> { new { id = ShipPaints.IdStandard, en = "標準", ja = (string?)null } };
                if (def != null) all.Add(new { id = ShipPaints.IdNone, en = "塗装なし", ja = (string?)null, ng = ng.GetValueOrDefault(ShipPaints.IdNone) });
                all.AddRange(list.Select(p => (object)new { id = p.Record, en = p.English, ja = p.Japanese, ng = ng.GetValueOrDefault(p.Record) }));
                items = all.ToArray();
                initial = saved != null && !ng.ContainsKey(saved) ? saved : "";
            }
        }
        Post(web, view, new { type = "paintList", ship = view.Short, items, initial, standardId });
    }

    // 材質の表を用意して、その URL を返す (作れなければ文を返す)
    private static async Task PaintTableAsync(WebView2 web, ShownShip view, string id)
    {
        try
        {
            var path = await ShipPaints.GetTableAsync(view.Short, view.Glb, id);
            Post(web, view, new { type = "paintTable", ship = view.Short, id, url = FileUrl(path) });
        }
        catch (Exception ex)
        {
            // unusable: 形の照合で出せない塗装 (ビューアは一覧で押せない形にする)
            Post(web, view, new { type = "paintTable", ship = view.Short, id, error = ex.Message, unusable = ex is ShipPaints.PaintUnusableException });
        }
    }

    // まだ同じ船を出していれば送る
    private static void Post(WebView2 web, ShownShip view, object msg)
    {
        if (web.CoreWebView2 == null || !Views.TryGetValue(web, out var now) || !ReferenceEquals(now, view)) return;
        web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(msg));
    }

    // 置き場所 (Ship3DExports.Folder。CacheHost に割り当ててある) の中のファイルの URL
    private static string FileUrl(string file)
    {
        var rel = Path.GetRelativePath(Ship3DExports.Folder, file).Replace('\\', '/');
        if (rel.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(rel)) throw new InvalidOperationException($"材質の表が置き場所の外にあります: {file}");
        long ticks = 0;
        try { ticks = File.GetLastWriteTimeUtc(file).Ticks; } catch { }
        return $"https://{Ship3DExports.CacheHost}/{string.Join('/', rel.Split('/').Select(Uri.EscapeDataString))}?v={ticks}";
    }

    // 船でない品の 3D の小さな絵を描く (DetailPanel.RenderThumbAsync。隠した WebView2 で開く)。ビューアは ?thumb=1 で 1 コマ描いて
    // chrome.webview.postMessage({type:'thumb', png:<PNG の data URL>}) を返す (失敗は {type:'error'})。
    // tablePath: 当てる材質の表 (色付けなし・古い版の GLB の標準の表。ShipPaints.ThumbTableId)。ビューアは &table=<URL> の表を当ててから描く
    public static void ShowThumb(WebView2 web, string shortName, string? glbPath = null, string? tablePath = null)
    {
        var core = web.CoreWebView2 ?? throw new InvalidOperationException("WebView2 が用意できていません");
        var url = ViewerUrl(core, shortName, glbPath, "") + "&item=1&thumb=1";
        if (tablePath != null) url += $"&table={Uri.EscapeDataString(FileUrl(tablePath))}";
        core.Navigate(url);
    }

    // ビューアの URL (キャッシュのフォルダを仮想ホストに割り当て直してから)。glbPath が null なら <短い名前>.glb
    private static string ViewerUrl(CoreWebView2 core, string shortName, string? glbPath, string title)
    {
        var folder = Ship3DExports.Folder;
        try { core.ClearVirtualHostNameToFolderMapping(Ship3DExports.CacheHost); } catch { }
        core.SetVirtualHostNameToFolderMapping(Ship3DExports.CacheHost, folder, CoreWebView2HostResourceAccessKind.Allow);
        long ticks = 0;
        var file = glbPath ?? Ship3DExports.GlbPath(shortName);
        try { ticks = File.GetLastWriteTimeUtc(file).Ticks; } catch { }
        var rel = Path.GetRelativePath(folder, file).Replace('\\', '/');
        if (rel.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(rel)) throw new InvalidOperationException($"3D モデルが置き場所の外にあります: {file}");
        // ?v= は書き出し直したときに前の GLB を使わないため (ファイルの更新日時)
        var glb = $"https://{Ship3DExports.CacheHost}/{string.Join('/', rel.Split('/').Select(Uri.EscapeDataString))}?v={ticks}";
        return $"https://{ViewerHost}/ship3d/viewer.html?glb={Uri.EscapeDataString(glb)}&title={Uri.EscapeDataString(title)}";
    }

    // 表示をやめる (読み込んだ GLB の分のメモリを放す)
    public static void Blank(WebView2 web)
    {
        Views.Remove(web);
        try { web.CoreWebView2?.NavigateToString("<html><body style=\"background:#1b1e24\"></body></html>"); } catch { }
    }
}
