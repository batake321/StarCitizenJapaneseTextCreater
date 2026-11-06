using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace StarCitizenJapaneseTextCreater;

// ステーションの地図の新しい方式 (StationView3D) の別ウィンドウ (モードレス。画面はコードで作る)。
// (2026-11-04、利用者の決定「Data.p4k から見る所の区域だけを、開いたときにその場で取り出して描く（前もってデータを作らない）」)
//   開くと: 場所の木 (Data.p4k の索引から) と地図の JSON (取り込み済みのもの。乗り場・階段・店・端末・階の名前) で StationView3D.Session を作り、
//     一時の置き場 (Ship3DExports.WorkFolder の下の view3d_<短い名前>_<GUID>\) に station.json を書いてビューア (wwwroot\stationmap\view3d.html) を開く。
//   ビューアが地点を頼むと ({type:'region', id, c, r}): まわり ±r m の区画だけを取り出して計算し (Session.BuildRegionAsync)、r<id>\ に書いて知らせる ({type:'regionReady'})。
//     取り出している間は {type:'regionProgress', done, total} を送る。ビューアが読み終えたら ({type:'regionLoaded'}) その地点の置き場を消す。窓を閉じたら一時の置き場を全部消す。
//   取り出しの同時数: 1 本は必ず動かし (CPU 使用率が 90% 以上の間は待つ。ゲームのアイコンの取り出しと同じ)、2 本目からは同時数の上限の決まり (Ship3DExports.TryAcquireExtraRun) で足す。
//   WebView2 の用意は船・ステーションの 3D と同じ Ship3DWeb.InitAsync。置き場は Ship3DExports.CacheHost (→ Ship3DExports.Folder) から fetch で読む
// (2026-11-04、利用者の指示「map に出し、別ウィンドウボタンで。」) 画面の中身は StationView3DView (Grid) にして、［マップ］タブの中 (MapPanel) と
// 別ウィンドウ (StationView3DWindow) の両方で同じものを使う
public sealed class StationView3DWindow : Window
{
    public StationView3DView View { get; private set; }
    public StationView3DWindow(string record, string name, Func<string, string, string>? shopLabel = null, Func<string, string, object?>? shopSales = null, string? focusShop = null)
    {
        View = new StationView3DView(record, name, shopLabel, shopSales, focusShop);
        Title = $"ステーションの 3D (新しい方式) - {View.PlaceTitle}";
        Width = 1400; Height = 900;
        Content = View;
        Closed += (_, _) => View.Close3D();
    }

    // (2026-11-04、利用者の指示「別ウィンドウひらいてたらそっちに」) 開いている窓に、別の場所 (か店の行き先) を出し直す: 今の 3D を閉じて新しい 3D に替え、窓を前に出す
    // activate: 窓を前に出すか (既定は出す)。現在位置のキーから来たとき (MapPanel.CaptureCurrentFromHotkeyAsync) は偽: 中身だけ替え、
    // 最小化も戻さず前にも出さない (ゲームからフォーカスを取らないため。2026-11-05)
    public void ShowPlace(string record, string name, Func<string, string, string>? shopLabel, Func<string, string, object?>? shopSales, string? focusShop, bool activate = true)
    {
        var old = View;
        View = new StationView3DView(record, name, shopLabel, shopSales, focusShop);
        Title = $"ステーションの 3D (新しい方式) - {View.PlaceTitle}";
        Content = View;
        old.Close3D();
        if (!activate) return;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
}

public sealed class StationView3DView : Grid
{
    // 場所の名前 (窓の題・［マップ］タブの帯に出す)
    public string PlaceTitle => _title;
    public string Record => _record;
    // ［マップ］タブの中に出している 3D か (ビューアに ?tab=1 を渡す。遠ざかり切ったら {type:'leave'} が来て LeaveRequested を起こす)
    public bool InTab { get; init; }
    public event Action? LeaveRequested;
    // 全画面の前の窓の形 (fullscreen の知らせ)
    private (WindowStyle Style, WindowState State)? _fullPrev;
    private readonly string _record, _shortName, _title;
    private readonly WebView2 _web = new();
    private readonly TextBlock _overlay = new() { Foreground = Brushes.White, FontSize = 28, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly CancellationTokenSource _cts = new();
    private CancellationTokenSource? _regionCts;
    private StationView3D.Session? _session;
    // マイハンガーの Session (site → Session。site は "hangar:" + ハンガーの socpak)。ステーションは site ""
    private readonly Dictionary<string, StationView3D.Session> _hangarSessions = new(StringComparer.Ordinal);
    private StationView3D.Loc? _loc;
    private StationView3D.Extractor? _extract;
    // 色付け (場所のパレットの色) を切った取り出し (OwnExtractor の palette: false)。ビューアの地点の頼み ({type:'region', …, pal}) の pal が false のときに使う
    // (2026-11-05、利用者の指示「1 で。右のスイッチに入れて自動保存にして」)。_palette は最後に頼まれた地点の色付け (先読みもそれで作る)
    private StationView3D.Extractor? _extractPlain;
    private bool _palette = true;
    // 保存の鍵に入れる区画の置き方の一覧の印 (色付けを切ったときは後ろに StationView3DCache.NoPaletteSuffix)
    private string LayoutIdOf(bool palette) => palette ? _layoutId : _layoutId + StationView3DCache.NoPaletteSuffix;
    private string _tempDir = "";
    private bool _ownInUse;

    // ===== 地点の取り出し結果の保存 (StationView3DCache) と先読み =====
    // (2026-11-04、利用者の指示「先読み、キャッシュ化 HDD 容量設定が空いていればファイル保存など早く動かす仕組みを入れて」)
    //   地点 (site・中心・半径) ごとの結果フォルダを、Data.p4k の印・区画の置き方の一覧の版が同じ間は保存して使い回す。ビューアに渡している間は
    //   lease (保存したもの = 消されないように印を付けたもの / 保存しなかったもの = 一時フォルダで、読み終えたら消す) を持つ。
    //   先読み: ビューアが何も頼まずに PrefetchIdleSeconds 秒たったら、今の場所のまわりの行き先 (station.json の targets) を近い順に、
    //   ビューアが使っている半径で裏で取り出して保存する (1 本ずつ・保存の上限を超えない・空きメモリが足りなければしない・ビューアが頼んだら止める)
    private string _p4kKey = "", _layoutId = "";
    private sealed record Lease(string? CacheKey, string? TempDir);
    private readonly Dictionary<int, Lease> _leases = new();
    private bool _viewerBusy;
    private double[]? _lastCenter;
    private readonly SortedSet<double> _radii = new();
    private List<(string Kind, string Name, double[] Pos)> _targets = new();
    private System.Threading.Timer? _idleTimer;
    private CancellationTokenSource? _prefetchCts;
    private Task? _prefetchTask;
    private int _prefetchBuilt;
    private const int PrefetchIdleSeconds = 20, PrefetchMaxBuilds = 12, PrefetchMaxCenters = 5;
    private const long PrefetchMinFreeMemory = 8L << 30, PrefetchEstimateBytes = 300L << 20;
    private const long PrewarmMinFreeMemory = 16L << 30, PrewarmMinBudget = 4L << 30;
    private const int PrewarmMaxCenters = 2;
    private const double PrefetchSeparation = 30;
    private readonly Func<string, string, string>? _shopLabel;
    private readonly Func<string, string, object?>? _shopSales;

    // shopLabel: 店 (CryGUID, Entity 名) → アプリの地図の店の名前。shopSales: 店 → 販売品の枠の知らせ ({type:'sales', title, shops}。地図の［MAP］の販売品の枠と同じもの)
    // focusShop: 開いたときの行き先にする店 (Entity 名。［詳細］の店の［MAP］)。無ければビューアの決まり
    private readonly string? _focusShop;
    public StationView3DView(string record, string name, Func<string, string, string>? shopLabel = null, Func<string, string, object?>? shopSales = null, string? focusShop = null)
    {
        Interlocked.Increment(ref s_open);
        lock (s_views) s_views.Add(this);
        _record = record; _shopLabel = shopLabel; _shopSales = shopSales; _focusShop = focusShop;
        _shortName = StationPlaces.ShortName(record);
        _title = string.IsNullOrWhiteSpace(name) ? _shortName : name;
        Background = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11));
        Children.Add(_web);
        Children.Add(_overlay);
        // 初めて画面に出たときに始める (［マップ］タブで隠してから出し直しても、始めるのは 1 回)
        Loaded += async (_, _) => { if (_started || _closed) return; _started = true; await StartAsync(); };
    }
    private bool _started;
    private bool _closed;

    // 開いているステーションの 3D の数 (作ってから Close3D するまで)。0 になったら部品の形の置き場も捨てる
    private static int s_open;
    // ステーションの 3D がどこかに開いているか (別ウィンドウ・［マップ］タブ)。開いている間は、同梱の新しい区画の置き方の一覧を置き場所へ写さない (StationView3DLayout.CopyNewerBundled)
    internal static bool AnyOpen => Volatile.Read(ref s_open) > 0;

    // 閉じる (窓を閉じた・［マップ］タブでほかの場所に替えた・星系図に戻った): 取り出しを止め、WebView2 を捨て、一時の置き場を消す。
    // (2026-11-04、利用者の指示「３D 表示後のメモリ後始末」) 区画の結果・使い回しの置き場 (Session) を手放し、3D がどこにも開いていなければ
    // 部品の形の置き場 (SocpakExporter) も捨てて GC で返す
    public void Close3D()
    {
        if (_closed) return;
        _closed = true;
        lock (s_views) s_views.Remove(this);
        _cts.Cancel();
        _regionCts?.Cancel();
        _idleTimer?.Dispose(); _idleTimer = null;
        _prefetchCts?.Cancel();
        ReleaseLeases();
        _bigCts?.Cancel();
        if (_bigLease is { } bl) { StationView3DCache.Current.Release(bl); _bigLease = null; }
        try { _web.Dispose(); } catch { }
        DeleteTemp();
        _session = null;
        _hangarSessions.Clear();
        _extract = null;
        _extractPlain = null;
        _loc = null;
        if (Interlocked.Decrement(ref s_open) <= 0)
        {
            s_open = 0;
            SocpakExporter.ReleaseShared();
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect();
    }

    private void Overlay(string text)
    {
        _overlay.Text = text;
        _overlay.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task StartAsync()
    {
        if (Ship3DExports.CurrentStamp() is not { } stamp) { _web.Visibility = Visibility.Collapsed; Overlay("ゲームの Data.p4k が見つかりません。設定タブのゲームのフォルダとチャンネルを確かめてください。"); return; }
        Overlay("準備中…");
        var ct = _cts.Token;
        try
        {
            _tempDir = Path.Combine(Ship3DExports.WorkFolder, $"view3d_{_shortName}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
            var record = _record;
            _session = await Task.Run(() =>
            {
                // 区画の置き方の一覧と場所の中身 (DB。LayoutFor)。2D の地図の JSON は使わない (2026-11-04、利用者の決定「名前を 2d からとるのをやめるだけの話」)
                var layout = LayoutFor(record, stamp, ct);
                _stationLayout = layout;
                var ini = Path.Combine(Path.GetDirectoryName(stamp.Path) ?? "", "data", "Localization", "japanese_(japan)", "global.ini");
                var loc = new StationView3D.Loc(ini, Path.Combine(DetailPanel.WorkDir(), "translations.db"));
                _loc = loc;
                var s = new StationView3D.Session(layout, "", loc, terminalLabel: StationTerminalKinds.Label, shopLabel: _shopLabel, texUrlBase: TexUrlBase);
                var stationJson = s.StationJson();
                File.WriteAllBytes(Path.Combine(_tempDir, "station.json"), stationJson);
                // 保存の鍵 (Data.p4k の印と区画の置き方の一覧の版・作った時刻・ゲームの画像の上限 (px。変えると画像の有無・細かさが変わるので別の鍵))・先読みの行き先
                _p4kKey = stamp.Key;
                _layoutId = LayoutIdNow();
                _targets = TargetsOf(stationJson);
                var cache = StationView3DCache.Current;
                cache.SweepStale(_p4kKey, _layoutId);
                return s;
            }, ct);
            // 外から見たときの印 (着陸パッド・ハンガー・ドッキングポート。StationView3D.ExteriorMarks)。裏で作り、ビューアの {type:'exteriorGet'} に {type:'exterior', …} で返す (OnMessage)
            if (_stationLayout is { } lay && _loc is { } lc) _exterior = Task.Run(() => StationView3D.ExteriorMarks(stamp.Path, lay, lc, ct), ct);
            _extract = StationView3D.OwnExtractor(stamp.Path,
                m => App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D (新しい方式)] 「{_title}」 {m}"),
                texMaxPx: TexMaxPx, texFolder: Path.Combine(Ship3DExports.Folder, StationView3DTextures.FolderName));
            _extractPlain = StationView3D.OwnExtractor(stamp.Path,
                m => App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D (新しい方式)] 「{_title}」 {m}"),
                texMaxPx: TexMaxPx, texFolder: Path.Combine(Ship3DExports.Folder, StationView3DTextures.FolderName), palette: false);
            await Ship3DWeb.InitAsync(_web);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _web.Visibility = Visibility.Collapsed;
            Overlay($"3D (新しい方式) を開けませんでした: {ex.Message}");
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D (新しい方式)] 「{_title}」を開けませんでした: {ex}");
            return;
        }
        var core = _web.CoreWebView2!;
        core.WebMessageReceived += OnMessage;
        var folder = Ship3DExports.Folder;
        try { core.ClearVirtualHostNameToFolderMapping(Ship3DExports.CacheHost); } catch { }
        core.SetVirtualHostNameToFolderMapping(Ship3DExports.CacheHost, folder, CoreWebView2HostResourceAccessKind.Allow);
        Overlay("");
        // URL の debug=1: 設定「マップのデバッグ表示」(App.Config.Station3DDebugView。既定オフ) が入っているとき。ビューアは下部の枠 (#pick) をこのときだけ出す (2026-11-05)
        core.Navigate($"https://{Ship3DWeb.ViewerHost}/stationmap/view3d.html?data={Uri.EscapeDataString(UrlOf(_tempDir))}&title={Uri.EscapeDataString(_title)}&app=1" + (InTab ? "&tab=1" : "") + (App.Config.Station3DDebugView ? "&debug=1" : "") + (_focusShop is { Length: > 0 } fs ? $"&goto={Uri.EscapeDataString(fs)}" : "")
            + (_exteriorRequested ? "&ext=1" : ""));   // 外から見る形で開くとき: 開いたときの行き先へ飛ばない (view3d.js の始め。2026-11-06)
    }

    // 保存の鍵に入れる区画の置き方の一覧の印 (色付けを入れたとき。StartAsync の _layoutId と、QT の先読み (PrefetchArrivalAsync) が使う)
    private static string LayoutIdNow() => (StationView3D.LayoutDbMeta(StationView3DLayout.DbPath, "format") ?? "") + "|" + (StationView3D.LayoutDbMeta(StationView3DLayout.DbPath, "built_utc") ?? "") + "|tex" + TexMaxPx.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ゲームの画像 (テクスチャ) の長い辺の上限 (px。設定「ステーションのテクスチャ (px)」。0 なら画像なし) と、ビューアが PNG を読む URL の頭 (2026-11-05)
    private static int TexMaxPx => Math.Max(0, App.Config.Station3DTextureMaxPx);
    private static string TexUrlBase => $"https://{Ship3DExports.CacheHost}/{StationView3DTextures.FolderName}/";

    // 置き場 (Ship3DExports.Folder) の下のフォルダの URL (最後に /)
    private static string UrlOf(string dir)
    {
        var rel = Path.GetRelativePath(Ship3DExports.Folder, dir).Replace('\\', '/');
        return $"https://{Ship3DExports.CacheHost}/{string.Join("/", rel.Split('/').Select(Uri.EscapeDataString))}/";
    }

    private void Post(object o)
    {
        try { _web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(o)); }
        catch (Exception ex) { App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D (新しい方式)] ビューアへ送れませんでした: {ex.Message}"); }
    }

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var r = doc.RootElement;
            var type = r.TryGetProperty("type", out var t) ? t.GetString() : null;
            // ビューアの失敗 (view3d.js の report: loadRegion・worker・promise など) は今まで捨てていて、読み込みが繰り返す等の原因が追えなかった (2026-11-05、利用者の報告「何度も作画中を繰り返してる」)。ログに残す
            if (type == "log") { App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーション 3D] {(r.TryGetProperty("message", out var lm) ? lm.GetString() : "")}"); return; }
            if (type == "error")
            {
                var src = r.TryGetProperty("source", out var es) ? es.GetString() : ""; var emsg = r.TryGetProperty("message", out var em) ? em.GetString() : "";
                App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーション 3D] ビューアの失敗 {src}: {emsg}");
                return;
            }
            if (type == "regionLoaded" && r.TryGetProperty("id", out var lid))
            {
                // ビューアが読み終えた地点の置き場を手放す (保存したものは印を外すだけで残す。保存しなかったものは消す)
                var lidv = lid.GetInt32();
                var d = Path.Combine(_tempDir, $"r{lidv}");
                try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                ReleaseLease(lidv);
                _viewerBusy = false;
                ScheduleIdle();
                return;
            }
            if (type == "wcacheUsed" && r.TryGetProperty("bytes", out var wb) && wb.TryGetInt64(out var wbytes)) { StationView3DCache.WorkerBytes = wbytes; return; }
            if (type == "prewarmDone") { _prewarmTcs?.TrySetResult(true); return; }
            if (type == "sales")
            {
                // 店の端末を押したとき (2026-11-04、利用者の指示「押したら売ってるリストと値段を出してほしい」): 端末の店 (地図の JSON の端末の shop・shopEntity) の販売品
                var g = r.TryGetProperty("guid", out var gv) ? gv.GetString() ?? "" : "";
                var en = r.TryGetProperty("entity", out var ev) ? ev.GetString() ?? "" : "";
                var msg = _shopSales?.Invoke(g, en);
                Post(msg ?? new { type = "sales", title = "販売品", shops = new List<object> { new { name = en, note = "販売データが紐づいていません。" } } });
                return;
            }
            // 全画面 (2026-11-06、利用者の指示「別ウィンドウの場合はファンクションキーで左のメニューも右のオンオフも消して全画面表示可能に」):
            // 別ウィンドウのときだけ、窓を枠なしの最大にする / 戻す (ビューアの F11 から {type:'fullscreen', on})
            if (type == "fullscreen")
            {
                if (!InTab && Window.GetWindow(this) is { } win)
                {
                    var on = r.TryGetProperty("on", out var ov) && ov.ValueKind == JsonValueKind.True;
                    if (on) { _fullPrev = (win.WindowStyle, win.WindowState); win.WindowState = WindowState.Normal; win.WindowStyle = WindowStyle.None; win.WindowState = WindowState.Maximized; }
                    else if (_fullPrev is { } p) { win.WindowStyle = p.Style; win.WindowState = p.State; _fullPrev = null; }
                }
                return;
            }
            if (type == "leave")
            {
                if (InTab) LeaveRequested?.Invoke();
                return;
            }
            if (type == "here")
            {
                await HereAsync();
                return;
            }
            // マイハンガーの大きさのうち、ハンガーの区画が決まるもの (2026-11-05、利用者の指示「ないものはエレベーターの行き先に出さない」。親の決定):
            // ビューアが station.json を読んだ後に {type:'hangarSizesGet'} で頼み、{type:'hangarSizes', sizes} で返す。sizes は、場所の入口 (place_hangars) の大きさのうち、
            // OpenHangarAsync と同じ StationHangarCatalog.Resolve で区画が決まるもの。ハンガーの表が作れない・読めないとき (GetAsync の例外) は返さない (ビューアは place_hangars の大きさのまま)
            // 現在位置の印 (開いたとき): 今の現在位置がこの場所の中なら {type:'hereMark'} で返す
            if (type == "hereMarkGet") { _pageReady = true; await RefreshHereMarkAsync(); if (_hereRequested) await ShowHereNoCaptureAsync(); else if (_exteriorRequested && !_closed) await PostExteriorViewAsync(); return; }
            if (type == "hangarSizesGet")
            {
                try
                {
                    if (Ship3DExports.CurrentStamp() is not { } hstamp) return;
                    var cat = await StationHangarCatalog.GetAsync(hstamp, _cts.Token);
                    var entrances = (_stationLayout?.Place?.Hangars ?? new()).Select(h => (h.Class, h.Size)).ToList();
                    var sizes = new[] { "S", "M", "L", "XL" }.Where(z => entrances.Any(e => e.Size == z) && cat.Resolve(_shortName, z, entrances, out _) != null).ToList();
                    if (!_closed) Post(new { type = "hangarSizes", sizes });
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D (新しい方式)] 「{_title}」のマイハンガーの大きさを決められませんでした (入口の大きさのまま出します): {ex.Message}"); }
                return;
            }
            if (type == "hangar")
            {
                await OpenHangarAsync(r.TryGetProperty("size", out var zv) ? zv.GetString() ?? "" : "");
                return;
            }
            // 外から見たときの印 (着陸パッド・ハンガー・ドッキングポート): ビューアが station.json を読んだ後に頼む。StartAsync で裏に作り始めたものを待って返す
            if (type == "exteriorGet")
            {
                if (_exterior is not { } ext) return;
                try { var o = await ext; if (!_closed) Post(o); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D (新しい方式)] 「{_title}」の外から見たときの印 (着陸パッド・ハンガー・ドッキングポート) を作れませんでした: {ex}"); }
                return;
            }
            if (type == "walkCheck")
            {
                await WalkCheckAsync(r);
                return;
            }
            // コメントの印 (StationNotes。2026-11-05、利用者「enter で文字入れて印付けるのは、いつでもできて入力したら即反映」「DB格納でいつでも」):
            //   {type:'notesGet', req} → {type:'notes', req, list:[{id,pos,text}]} (この場所の全部。pos は場所の座標)
            //   {type:'noteAdd', pos, text, tmp} → DB に入れて {type:'noteAdded', id, pos, text, tmp} (tmp はビューアが印を結び付けるための番号。そのまま返す)
            //   {type:'noteDelete', id} → DB から消す。失敗は {type:'noteError', message} とログ
            if (type is "notesGet" or "noteAdd" or "noteDelete" or "noteUpdate")
            {
                await NoteMessageAsync(type, r);
                return;
            }
            if (type == "bigGet") { await BigGetAsync(r); return; }
            if (type != "region" || _session == null || _extract == null || _extractPlain == null) return;
            // pal: 色付け (場所のパレットの色) を当てるか (ビューアの右上のスイッチ「色付け」。無ければ当てる)
            var palette = !(r.TryGetProperty("pal", out var pv) && pv.ValueKind == JsonValueKind.False);
            _palette = palette;
            var layoutId = LayoutIdOf(palette);
            var site = r.TryGetProperty("site", out var sv) ? sv.GetString() ?? "" : "";
            var siteSession = site.Length == 0 ? _session : _hangarSessions.GetValueOrDefault(site);
            if (siteSession == null) { Post(new { type = "regionError", id = r.GetProperty("id").GetInt32(), message = "マイハンガーの用意が終わっていません" }); return; }
            var id = r.GetProperty("id").GetInt32();
            var c = r.GetProperty("c").EnumerateArray().Select(x => x.GetDouble()).ToArray();
            var rad = r.GetProperty("r").GetDouble();
            _regionCts?.Cancel();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _regionCts = cts;
            var dir = Path.Combine(_tempDir, $"r{id}");
            var progress = new Progress<(int Done, int Total)>(p => Post(new { type = "regionProgress", id, done = p.Done, total = p.Total }));
            // ビューアが頼んだので、先読みは止める。前の地点の手放していない置き場 (読み込みに失敗して regionLoaded が来なかったもの) も手放す
            _viewerBusy = true; _lastCenter = c; _radii.Add(rad); _walkCenters[site] = c;
            if (site.Length == 0) lock (s_seenLock) { s_seenRadii.Add(rad); s_seenPal = palette; }
            _idleTimer?.Dispose(); _idleTimer = null;
            await StopPrefetchAsync();
            ReleaseLeases();
            try
            {
                var session = siteSession; var extract = palette ? _extract : _extractPlain;
                var cache = StationView3DCache.Current;
                var siteKey = site.Length == 0 ? _record : site;
                var key = cache.Enabled ? StationView3DCache.KeyOf(siteKey, _p4kKey, layoutId, c, rad) : "";
                // 保存したものがあれば、それをそのままビューアに渡す (取り出し・計算をしない)
                if (key.Length > 0 && cache.TryGet(key, siteKey, _p4kKey, layoutId, c, rad) is { } hit)
                {
                    _leases[id] = new Lease(key, null);
                    Post(WithWorkerCache(new { type = "regionReady", id, @base = UrlOf(hit.Dir), extractSeconds = 0.0, computeSeconds = 0.0, seconds = 0.0, socpaks = hit.Socpaks, missing = hit.Missing, cached = true }, cache, key));
                    return;
                }
                // 保存できそうなら、保存先の書きかけの場所に作る (できたら名前を替えて保存にする。ビューアはそのまま読む)
                var stage = key.Length > 0 && cache.CanStore(PrefetchEstimateBytes) ? cache.BeginStage(key) : null;
                var st = await Task.Run(() => session.BuildRegionAsync(c, rad, stage ?? dir, extract, AcquireAsync, progress, cts.Token, palette), cts.Token);
                var served = stage ?? dir; var stored = false;
                if (stage != null)
                {
                    if (cache.Commit(stage, key, siteKey, _p4kKey, layoutId, c, rad, st.Socpaks, st.Missing) is { } saved) { served = saved.Dir; _leases[id] = new Lease(key, null); stored = true; }
                    else _leases[id] = new Lease(null, stage);   // 保存できなかった (上限・空き): 読み終えたら消す
                }
                Post(WithWorkerCache(new { type = "regionReady", id, @base = UrlOf(served), extractSeconds = st.ExportSeconds, computeSeconds = st.ComputeSeconds, seconds = st.BuildSeconds, socpaks = st.Socpaks, missing = st.Missing }, cache, stored ? key : ""));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D (新しい方式)] 「{_title}」の地点を作れませんでした: {ex}");
                Post(new { type = "regionError", id, message = ex.Message });
                _viewerBusy = false; ScheduleIdle();
            }
        }
        catch (Exception ex)
        {
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D (新しい方式)] ビューアからの知らせを扱えませんでした: {ex.Message}");
        }
    }

    // ===== 歩くモードの裏の補い (2026-11-05、利用者の指示「歩くモードで近く 5 m 以内の部品を裏で先に細かい形で調べる 見つけたら DB に入れる」
    // 「そのエリアに荒いので部品がない場合、細かいのでチェック」「床でなく物体で」) =====
    // ビューアが 1 秒ごとに知らせる、粗い形に物体の無い升 ({type:'walkCheck', pos, zTol, cells, site}。場所の座標) を、裏 (Task.Run) で細かい形で調べる (Session.WalkCheck)。
    // 見つかった三角形は view3d_fill_walk.db に入れ (StationFill.WriteWalk)、ビューアに {type:'walkFill', center, items:[{inst, name, tris}]} で返す
    // (tris は地点の中心 center からの相対。center は、その site で最後に BuildRegionAsync に渡した中心)。同時に 1 本だけ (順に待つ)。失敗はログに残して続ける
    private readonly SemaphoreSlim _walkGate = new(1, 1);
    private readonly Dictionary<string, double[]> _walkCenters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StationView3D.Session.WalkFillState> _walkStates = new(StringComparer.Ordinal);
    private async Task WalkCheckAsync(JsonElement r)
    {
        if (_closed || Ship3DExports.CurrentStamp() is not { } stamp) return;
        var site = r.TryGetProperty("site", out var sv) ? sv.GetString() ?? "" : "";
        var session = site.Length == 0 ? _session : _hangarSessions.GetValueOrDefault(site);
        if (session == null || !_walkCenters.TryGetValue(site, out var center)) return;
        if (!r.TryGetProperty("pos", out var pv) || pv.ValueKind != JsonValueKind.Array || pv.GetArrayLength() != 3 || !r.TryGetProperty("cells", out var cv) || cv.ValueKind != JsonValueKind.Array) return;
        var pos = pv.EnumerateArray().Select(x => x.GetDouble()).ToArray();
        var zTol = r.TryGetProperty("zTol", out var zv) && zv.ValueKind == JsonValueKind.Number ? zv.GetDouble() : 0.5;
        var cells = cv.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.Array && c.GetArrayLength() >= 2).Select(c => c.EnumerateArray().Select(x => x.GetDouble()).ToArray()).ToList();
        if (cells.Count == 0) return;
        if (!_walkStates.TryGetValue(site, out var state)) _walkStates[site] = state = new StationView3D.Session.WalkFillState();
        var ct = _cts.Token; var title = _title; var p4kPath = stamp.Path;
        void Log(string m) => App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D (新しい方式)] 「{title}」の歩くモードの補い: {m}");
        try { await _walkGate.WaitAsync(ct); } catch (OperationCanceledException) { return; }
        try
        {
            var items = await Task.Run(() =>
            {
                var found = session.WalkCheck(p4kPath, pos, zTol, cells, state, Log, ct);
                foreach (var it in found)
                {
                    ct.ThrowIfCancellationRequested();
                    try { StationFill.WriteWalk(StationFill.WalkDbPath, it.Kind, it.Sig, it.LocalTris); }
                    catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException or InvalidDataException) { Log($"DB に入れられませんでした ({it.Kind}): {ex.Message}"); }
                }
                return found;
            }, ct);
            if (_closed || items.Count == 0) return;
            Post(new
            {
                type = "walkFill",
                center,
                items = items.Select(it =>
                {
                    var t = new float[it.WorldTris.Length];
                    for (int i = 0; i < t.Length; i++) t[i] = (float)(it.WorldTris[i] - center[i % 3]);
                    return new { inst = it.Inst, name = it.Name, tris = t };
                }).ToList(),
            });
            Log($"細かい形で {items.Count} 部品・{items.Sum(i => i.WorldTris.Length / 9)} 三角形を補いました (升 {cells.Count}・高さ {pos[2]:F2} ±{zTol:F2} m): " + string.Join(", ", items.Take(8).Select(i => $"{i.Name} (inst {i.Inst})")) + (items.Count > 8 ? " ほか" : ""));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log($"失敗しました: {ex}"); }
        finally { _walkGate.Release(); }
    }

    // コメントの印の知らせ (上の OnMessage の説明)。DB の読み書きは裏 (Task.Run)。マイハンガーの中でも record は同じ (座標は場所の座標。親の決定)
    private async Task NoteMessageAsync(string type, JsonElement r)
    {
        var record = _record;
        try
        {
            if (type == "notesGet")
            {
                var req = r.TryGetProperty("req", out var rq) && rq.ValueKind == JsonValueKind.Number ? rq.GetInt64() : 0;
                var list = await Task.Run(() => StationNotes.Read(record));
                if (_closed) return;
                Post(new { type = "notes", req, list = list.Select(n => new { id = n.Id, pos = n.Pos, text = n.Text }).ToList() });
            }
            else if (type == "noteAdd")
            {
                if (!r.TryGetProperty("pos", out var pv) || pv.ValueKind != JsonValueKind.Array || pv.GetArrayLength() != 3) return;
                var pos = pv.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                var text = r.TryGetProperty("text", out var tv) ? tv.GetString() ?? "" : "";
                if (text.Length == 0) return;
                var tmp = r.TryGetProperty("tmp", out var mv) && mv.ValueKind == JsonValueKind.Number ? mv.GetInt64() : 0;
                var id = await Task.Run(() => StationNotes.Add(record, pos, text));
                if (_closed) return;
                Post(new { type = "noteAdded", id, pos, text, tmp });
            }
            else if (type == "noteUpdate")
            {
                // 文を直す (2026-11-06、利用者の指示「削除だけでなく編集できるようにしてほしい」)
                if (!r.TryGetProperty("id", out var uv) || uv.ValueKind != JsonValueKind.Number) return;
                var text = r.TryGetProperty("text", out var tv) ? tv.GetString() ?? "" : "";
                if (text.Length == 0) return;
                var id = uv.GetInt64();
                await Task.Run(() => StationNotes.Update(id, text));
            }
            else if (type == "noteDelete")
            {
                if (!r.TryGetProperty("id", out var iv) || iv.ValueKind != JsonValueKind.Number) return;
                var id = iv.GetInt64();
                await Task.Run(() => StationNotes.Delete(id));
            }
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D (新しい方式)] 「{_title}」のコメントの印 ({type}) を扱えませんでした: {ex.Message}");
            if (!_closed) Post(new { type = "noteError", message = ex.Message });
        }
    }

    // ===== 場所の全部の大きな物体 (2026-11-06、利用者の指示「距離に関係なく大きなびったいは書いた方がいいのかも」。親の決定) =====
    // ビューアの {type:'bigGet', site, pal, s3}: 保存 (StationView3DCache の FindBig。境が s3 以下のもの) があればそれを、無ければ裏で作って (Session.BuildBigAsync。場所ごとに 1 回) 保存し、
    // {type:'bigReady', site, pal, s3 (作った境), base} を返す。作っている間もビューアは今のまま動く (待たない)。地点を作っている間 (_viewerBusy) は区画の取り出しを待つ。
    // 作るのにかかった時間はログに 1 行出す。保存しない設定・保存できないとき (上限・空き) は一時の置き場に作り、窓を閉じたら消す
    private CancellationTokenSource? _bigCts;
    private string? _bigLease, _bigBuilding;
    private int _bigSeq;
    private async Task BigGetAsync(JsonElement r)
    {
        var site = r.TryGetProperty("site", out var sv) ? sv.GetString() ?? "" : "";
        var palette = !(r.TryGetProperty("pal", out var pv) && pv.ValueKind == JsonValueKind.False);
        var s3 = r.TryGetProperty("s3", out var tv) && tv.TryGetDouble(out var t3) && t3 >= 0 ? t3 : 20;
        if (site.Length == 0) lock (s_seenLock) s_seenS3 = s3;
        var session = site.Length == 0 ? _session : _hangarSessions.GetValueOrDefault(site);
        var extract = palette ? _extract : _extractPlain;
        if (session == null || extract == null) { Post(new { type = "bigError", site, pal = palette, message = "用意が終わっていません" }); return; }
        var cache = StationView3DCache.Current;
        var siteKey = site.Length == 0 ? _record : site;
        var layoutId = LayoutIdOf(palette);
        if (cache.Enabled && cache.FindBig(siteKey, _p4kKey, layoutId, s3, out var thr, out var hkey) is { } hit)
        {
            if (_bigLease is { } old) cache.Release(old);
            _bigLease = hkey;
            Post(new { type = "bigReady", site, pal = palette, s3 = thr, @base = UrlOf(hit.Dir), cached = true });
            return;
        }
        // 同じものを作っている最中なら、そのまま待つ (終わったら返す)。別のもの (場所・色付け・境が違う) を作っていたら、やめて作り直す
        var want = $"{siteKey}|{layoutId}|{s3.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}";
        if (_bigBuilding == want) return;
        _bigCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _bigCts = cts; _bigBuilding = want;
        var key = StationView3DCache.KeyOfBig(siteKey, _p4kKey, layoutId, s3);
        var stage = cache.Enabled && cache.CanStore(PrefetchEstimateBytes) ? cache.BeginStage(key) : null;
        var dir = stage ?? Path.Combine(_tempDir, $"big{++_bigSeq}");
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            async Task Pause(CancellationToken ct) { while (await Dispatcher.InvokeAsync(() => _viewerBusy)) await Task.Delay(500, ct); }
            var st = await Task.Run(() => session.BuildBigAsync(s3, dir, extract, AcquireAsync, Pause, cts.Token), cts.Token);
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D (新しい方式)] 「{_title}」の場所の全部の大きな物体 (境 {s3} m) を作りました: {sw.Elapsed.TotalSeconds:F1} 秒・区画 {st.Socpaks} のうち {st.Kept}・部品 {st.Parts}・{st.Bytes / 1048576.0:F0} MB");
            var served = dir;
            if (stage != null && cache.Commit(stage, key, siteKey + StationView3DCache.BigSuffix, _p4kKey, layoutId, [s3], 0, st.Socpaks, 0) is { } saved)
            {
                if (_bigLease is { } old) cache.Release(old);
                _bigLease = key; served = saved.Dir;
            }
            if (!_closed && ReferenceEquals(_bigCts, cts)) Post(new { type = "bigReady", site, pal = palette, s3, @base = UrlOf(served), seconds = sw.Elapsed.TotalSeconds });
        }
        catch (OperationCanceledException) { if (stage != null) StationView3DCache.TryDelete(stage); }
        catch (Exception ex)
        {
            if (stage != null) StationView3DCache.TryDelete(stage);
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D (新しい方式)] 「{_title}」の場所の全部の大きな物体を作れませんでした: {ex}");
            if (!_closed) Post(new { type = "bigError", site, pal = palette, message = ex.Message });
        }
        finally { if (ReferenceEquals(_bigCts, cts)) _bigBuilding = null; }
    }

    // ===== 保存したものの手放し・先読み (上の説明) =====
    private void ReleaseLease(int id)
    {
        if (!_leases.Remove(id, out var l)) return;
        if (l.CacheKey != null) StationView3DCache.Current.Release(l.CacheKey);
        else if (l.TempDir != null) StationView3DCache.TryDelete(l.TempDir);
    }
    private void ReleaseLeases() { foreach (var id in _leases.Keys.ToList()) ReleaseLease(id); }

    // station.json の targets のうち、位置のあるもの (種類・名前・位置)
    private static List<(string Kind, string Name, double[] Pos)> TargetsOf(byte[] stationJson)
    {
        var res = new List<(string, string, double[])>();
        try
        {
            using var doc = JsonDocument.Parse(stationJson);
            if (!doc.RootElement.TryGetProperty("targets", out var ts) || ts.ValueKind != JsonValueKind.Array) return res;
            foreach (var t in ts.EnumerateArray())
            {
                if (!t.TryGetProperty("pos", out var p) || p.ValueKind != JsonValueKind.Array || p.GetArrayLength() != 3) continue;
                var pos = p.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                res.Add((t.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "", t.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "", pos));
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { }
        return res;
    }

    // ビューアが何も頼まずに PrefetchIdleSeconds 秒たったら、先読みを始める
    private void ScheduleIdle()
    {
        _idleTimer?.Dispose(); _idleTimer = null;
        if (_closed || _viewerBusy || _prefetchBuilt >= PrefetchMaxBuilds || !StationView3DCache.Current.Enabled) return;
        _idleTimer = new System.Threading.Timer(_ => Dispatcher.BeginInvoke(StartPrefetch), null, TimeSpan.FromSeconds(PrefetchIdleSeconds), Timeout.InfiniteTimeSpan);
    }

    // 地点の知らせ (regionReady) に、解析の結果の保存 (ビューアの WC) の鍵・使ってよい大きさ・ドライブの空きを足す。
    // 鍵は地点の保存の鍵 (地点が保存されたときだけ。保存されていない地点は解析の結果も保存しない)。大きさ 0 = 保存しない設定 (ビューアは今ある保存を消す)
    private static object WithWorkerCache(object msg, StationView3DCache cache, string key)
    {
        var budget = cache.WorkerBudget();
        var free = StationView3DCache.WorkerFreeBytes();
        var o = System.Text.Json.JsonSerializer.SerializeToNode(msg)!.AsObject();
        o["wkey"] = key.Length > 0 && budget > 0 ? key : null;
        o["wbudget"] = budget;
        o["wfree"] = free;
        return o;
    }

    private TaskCompletionSource<bool>? _prewarmTcs;

    private async Task StopPrefetchAsync()
    {
        if (_prewarmTcs != null) { _prewarmTcs.TrySetCanceled(); Post(new { type = "prewarmCancel" }); }
        _prefetchCts?.Cancel();
        var t = _prefetchTask;
        if (t != null) { try { await t; } catch (OperationCanceledException) { } catch (Exception) { } }
        _prefetchTask = null;
    }

    private void StartPrefetch()
    {
        if (_closed || _viewerBusy || _prefetchTask != null || _session == null || _extract == null || _extractPlain == null || _lastCenter == null || _radii.Count == 0) return;
        if (_prefetchBuilt >= PrefetchMaxBuilds || !StationView3DCache.Current.Enabled) return;
        // 先読みは、最後に頼まれた地点と同じ色付け (_palette) で作る
        var palette = _palette;
        var center = _lastCenter; var radii = _radii.ToList(); var session = _session; var extract = palette ? _extract : _extractPlain;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _prefetchCts = cts;
        // ビューアが頼んだ site "" (ステーション本体) の行き先だけ。ハンガーの中は先読みしない
        _prefetchTask = Task.Run(() => PrefetchAsync(session, extract, center, radii, palette, cts.Token));
    }

    // 解析の結果 (ビューアの WC) の先読み: ビューアに別の糸で解析して保存だけしてもらう。
    // 解析は 1 地点で数 GB のメモリを使う (Seraphim で 5.5 GB ほど) ので、空き物理メモリが PrewarmMinFreeMemory 以上のとき、
    // 解析の結果に使ってよい大きさが PrewarmMinBudget 以上で、ドライブの空きがその分あるときだけ頼む。ビューアが地点を頼んだら止める
    private async Task PrewarmAsync(StationView3DCache cache, string dir, string key, double[] c, double r, CancellationToken ct)
    {
        if (!cache.Enabled || _closed) return;
        var budget = cache.WorkerBudget();
        if (budget < PrewarmMinBudget) return;
        var free = StationView3DCache.WorkerFreeBytes();
        if (free is long f && f - PrewarmMinBudget < StationView3DCache.FreeReserveBytes) return;
        if (Ship3DExports.CpuHighNow) return;
        if (Ship3DExports.AvailablePhysicalMemory() is not long mem || mem < PrewarmMinFreeMemory) return;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.InvokeAsync(() =>
        {
            if (_closed || _viewerBusy) { tcs.TrySetResult(false); return; }
            _prewarmTcs = tcs;
            Post(new { type = "prewarm", pid = 1, @base = UrlOf(dir), key, c, r, budget, free });
        });
        try
        {
            using var reg = ct.Register(() => tcs.TrySetCanceled());
            await tcs.Task.WaitAsync(TimeSpan.FromMinutes(20), ct);
        }
        finally { await Dispatcher.InvokeAsync(() => { if (ReferenceEquals(_prewarmTcs, tcs)) _prewarmTcs = null; }); }
    }

    // 近い行き先から順に、ビューアが使った半径 (小さい方から) で作って保存する。1 本ずつ。次の場合は止める:
    //   ビューアが頼んだ (取り消し)・CPU 使用率が 90% 以上・空きメモリが PrefetchMinFreeMemory 未満・保存の上限を超えそう (古いものを消してまで先読みしない)・空きが足りない
    private async Task PrefetchAsync(StationView3D.Session session, StationView3D.Extractor extract, double[] from, List<double> radii, bool palette, CancellationToken ct)
    {
        var layoutId = LayoutIdOf(palette);
        var cache = StationView3DCache.Current;
        try
        {
            // 行き先: 今の中心から近い順。同じ所 (PrefetchSeparation m 以内) は 1 つにまとめる。今の中心のまわりは、ビューアが読んだので除く
            var chosen = new List<double[]> { from };
            var plan = new List<double[]>();
            double Dist(double[] a, double[] b) => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));
            foreach (var t in _targets.OrderBy(t => Dist(t.Pos, from)))
            {
                if (chosen.Any(c => Dist(c, t.Pos) <= PrefetchSeparation)) continue;
                chosen.Add(t.Pos); plan.Add(t.Pos);
                if (plan.Count >= PrefetchMaxCenters) break;
            }
            int prewarmed = 0;
            foreach (var r in radii)
            {
                foreach (var c in plan)
                {
                    ct.ThrowIfCancellationRequested();
                    if (_prefetchBuilt >= PrefetchMaxBuilds || !cache.Enabled) return;
                    // CPU が高い間は待つ (ゲームを動かしているとき)。長く続くなら諦める
                    for (int w = 0; Ship3DExports.CpuHighNow; w++) { if (w >= 12) return; await Task.Delay(5000, ct); }
                    if (Ship3DExports.AvailablePhysicalMemory() is long free && free < PrefetchMinFreeMemory) return;
                    var key = StationView3DCache.KeyOf(_record, _p4kKey, layoutId, c, r);
                    var hit = cache.TryGet(key, _record, _p4kKey, layoutId, c, r);
                    if (hit == null)
                    {
                        // 古いものを消してまで先読みしない
                        if (!cache.CanStore(PrefetchEstimateBytes) || cache.UsedBytes() + PrefetchEstimateBytes > StationView3DCache.MaxBytesSetting) return;
                        var stage = cache.BeginStage(key);
                        var st = await session.BuildRegionAsync(c, r, stage, extract, AcquireAsync, null, ct, palette);
                        ct.ThrowIfCancellationRequested();
                        hit = cache.Commit(stage, key, _record, _p4kKey, layoutId, c, r, st.Socpaks, st.Missing);
                        if (hit == null) StationView3DCache.TryDelete(stage);
                        Interlocked.Increment(ref _prefetchBuilt);
                        App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D (新しい方式)] 「{_title}」を先読みして保存しました: 中心 {string.Join(",", c.Select(v => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)))} ±{r} m ({st.BuildSeconds:F1} 秒)");
                    }
                    if (hit == null) continue;
                    try
                    {
                        // 解析の結果 (worker) の先読み: いちばん小さい半径の、近い PrewarmMaxCenters か所だけ。メモリに余裕があるときだけ (PrewarmAsync)
                        if (r == radii[0] && prewarmed < PrewarmMaxCenters) { prewarmed++; await PrewarmAsync(cache, hit.Dir, key, c, r, ct); }
                    }
                    finally { cache.Release(key); }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D (新しい方式)] 「{_title}」の先読みを止めました: {ex.Message}");
        }
        finally
        {
            // 取り消しで書きかけが残っていれば BuildRegionAsync が消している。止めた後、ビューアが静かなままなら次の機会にまた始める
            try { await Dispatcher.InvokeAsync(() => { _prefetchTask = null; }); } catch (Exception) { }   // 窓を閉じた後は Dispatcher が止まっている
        }
    }

    // マイハンガー (2026-11-04、利用者の指示「マイハンガーの S M L XL がない」): 旧アプリ (StationMapPanel.ShowHangarAsync) と同じく、
    // StationHangarCatalog.Resolve (場所の既定のハンガー → 入口の class) でハンガーを選び、ハンガーの地図を用意 (StationMapExports.EnsureHangarAsync。無ければ取り込む) して、
    // その区画の置き方の一覧 (無ければゲームデータの索引と場所の木から作る) で Session を作る。ビューアには {type:'hangarReady', site, data, title, how} を返す
    private async Task OpenHangarAsync(string size)
    {
        var title = $"マイハンガー（{size}）";
        try
        {
            if (_loc == null) throw new InvalidOperationException("まだ準備中です");
            if (Ship3DExports.CurrentStamp() is not { } stamp) throw new InvalidOperationException("ゲームの Data.p4k が見つかりません。設定タブのゲームのフォルダとチャンネルを確かめてください。");
            var cat = await StationHangarCatalog.GetAsync(stamp, _cts.Token);
            // ハンガーの入口 (class と大きさ) は、場所の中身 (DB) から (2D の地図は使わない)
            var entrances = (_stationLayout?.Place?.Hangars ?? new()).Select(h => (h.Class, h.Size)).ToList();
            var choice = cat.Resolve(_shortName, size, entrances, out var note);
            if (choice == null) throw new InvalidOperationException(note);
            var site = "hangar:" + choice.Socpak;
            var dir = Path.Combine(_tempDir, "h" + _hangarSessions.Count);
            if (!_hangarSessions.TryGetValue(site, out var hs))
            {
                var hrec = StationHangars.RecordOf(choice.Socpak);
                var loc = _loc; var ct = _cts.Token;
                hs = await Task.Run(() => new StationView3D.Session(LayoutFor(hrec, stamp, ct), "", loc, terminalLabel: StationTerminalKinds.Label, texUrlBase: TexUrlBase), ct);
                Directory.CreateDirectory(dir);
                await File.WriteAllBytesAsync(Path.Combine(dir, "station.json"), hs.StationJson(), _cts.Token);
                _hangarSessions[site] = hs;
                _hangarDirs[site] = dir;
            }
            Overlay("");
            var name = StationView3D.HangarTitle(_loc, choice.NameKey);
            Post(new { type = "hangarReady", site, data = UrlOf(_hangarDirs[site]), title = name.Length > 0 ? name : title, how = choice.How });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D (新しい方式)] 「{_title}」の{title}を開けませんでした: {ex}");
            Post(new { type = "hangarError", message = ex.Message });
        }
    }
    private readonly Dictionary<string, string> _hangarDirs = new(StringComparer.Ordinal);

    // ===== ［現在位置］ (2026-11-04、利用者の指示「2D 地図は消して」で、2D の地図の［現在位置］を 3D に向け直した) =====
    // ［マップ］タブの［現在位置］と同じ処理 (MapPanel.CaptureCurrentAsync。/showlocation を送って App.Config.MapCurrent を入れ替える)。MainWindow がつなぐ
    public static Func<string, Task<(bool Ok, string Status)>>? CaptureHook { get; set; }
    // 取った座標 (星系の座標) を、この場所のルート (StationRoot.ToLocal) で場所の座標に直す。自転する場所 (都市) は、取った時刻の自転の角度
    // (StationRoot.CorrectedTheta。データの式 + 設定の補正値 MapSpinCorrections) を使い、さらに取った時刻の前後 HereFitSeconds 秒を HereFitStep 秒ずつずらして、
    // 点が区画の箱 (一覧の置かれた所の箱のうち、大きさ HereBoxMax m 未満のもの) の中に入る時刻のうち、取った時刻にいちばん近いものを使う
    // (2D の地図は床の上に入る時刻を探していた (FitStanding)。3D は床の判定を前もって持たないので、区画の箱で代える。1 秒で点が数百 m〜2 km 動くため)。
    // ビューアには {type:'here', ok, pos, note} を返す (pos は場所の座標)
    private const double HereFitSeconds = 1.5, HereFitStep = 0.02, HereBoxMax = 500;
    private StationRoot? _root;
    // 場所のルートを探す 1 本 (場所の record と Data.p4k の印ごと。結果が null でも作り直さない。3D と［マップ］タブの現在地の文で共有)
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<StationRoot?>> s_rootTasks = new(StringComparer.Ordinal);
    private static Task<StationRoot?> RootOfAsync(string record, Ship3DExports.P4kStamp stamp) => s_rootTasks.GetOrAdd(stamp.Key + "|" + record, _ => Task.Run(() =>
    {
        if (StationHangars.IsRecord(record)) return StationHangars.RootOf(record);
        using var index = StationIndex.Build(stamp.Path);
        var places = StationPlaces.LoadPlaces(DetailPanel.DbPath()).Select(p => p.Place).ToList();
        return index.FindRoots(places).Roots.TryGetValue(record, out var r) ? r : null;
    }));

    // ===== 開こうとした場所の差し替え (2026-11-05、親の決定。MapPanel.RoutePlaceAsync から。店の行き先の指定が無いときだけ) =====
    // (利用者の報告「HUR-L5 星系からクリックして出ない」「HUR-L5 データちゃんとしてない」)
    // 1. 行き先が 1 つも無い場所 (AmenityMapHints.Hints.Targets が偽。例: ラグランジュ点 StarMapObject.Stanton1_L5「HUR L5」。区画はガス雲と小惑星だけ):
    //    その場所の区画の箱 (全部の区画の箱を合わせた範囲。StationView3D.LayoutDbBox。場所の座標) の中に、同じ星系の行き先のある場所
    //    (星図の位置を、開こうとした場所のルートで場所の座標に直して見る) があれば、その中で、開こうとした場所の星図の位置からいちばん近い場所を代わりに開く。
    //    箱の中に無ければ差し替えない (今までどおりその場所を開く)。
    //    行き先のある場所のうち、星図の親をたどった先にも行き先のある場所があるもの (ステーションの中のクリニックなど。親の区画の一部) は候補にしない
    //    (HUR L5 からは HUR-L5 High Course Clinic のほうが HUR-L5 High Course Station より約 0.6 km 近いため)。
    //    自転する場所は、今の時刻の自転の角度 (補正を足したもの。場所の位置 CurrentLocation.PlacePosition と同じ角度) で直す。角度が決まらなければ差し替えない
    // 2. 行き先のある場所で、星図の親も行き先のある場所で、その場所の区画が親の区画の中に入っているもの (例: StarMapObject.RR_HUR_L5_CLINIC「HUR-L5 High Course Clinic」。
    //    その場所だけの 3D には、親のステーションの本体・着陸パッド・ハンガーが無い): 親の場所を、その場所の最初の店を行き先にして開く
    //    (StationView3D.LayoutDbChildFocus で行き先が決まるときだけ。決まらなければ差し替えない)
    // Status: 差し替えたことを知らせる 1 行 (［マップ］タブの状態の欄に出す)
    internal sealed record PlaceRedirect(ShopTreeNode Node, string? FocusShop, string Status);
    internal static async Task<PlaceRedirect?> RedirectAsync(string record, string name, ShopTree tree)
    {
        try
        {
            var hints = await Task.Run(AmenityMapHints.All);
            if (!hints.TryGetValue(record, out var h) || tree.NodeByStarmapRecord(record) is not { } node) return null;
            bool HasTargets(ShopTreeNode n) => hints.TryGetValue(n.RecordName, out var x) && x.Targets;
            var db = StationView3DLayout.DbPath;
            if (h.Targets)
            {
                if (node.Parent is not { } parent || !HasTargets(parent)) return null;
                var parentRecord = parent.RecordName;
                var focus = await Task.Run(() => StationView3D.LayoutDbChildFocus(db, record, parentRecord));
                return focus == null ? null : new PlaceRedirect(parent, focus, $"{name} は {parent.Name} の中にあるので、{parent.Name} の 3D でその場所を出しました");
            }
            if (!node.Position.HasValue || Ship3DExports.CurrentStamp() is not { } stamp) return null;
            if (await Task.Run(() => StationView3D.LayoutDbBox(db, record)) is not { } box) return null;
            if (await RootOfAsync(record, stamp) is not { } root) return null;
            var unix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            double theta = 0;
            if (root.Spins)
            {
                if (BodyRecordOfUuid(root.SpinUuid) is not { } body || root.CorrectedTheta(unix, body, App.Config.MapSpinCorrections) is not { } th) return null;
                theta = th;
            }
            var system = node.Root;
            var same = tree.Places.Where(p => p.Root == system).ToList();
            var thetas = CurrentLocation.ThetasAt(same, unix);
            bool UnderTargets(ShopTreeNode n) { for (var a = n.Parent; a != null; a = a.Parent) if (HasTargets(a)) return true; return false; }
            var cands = same.Where(p => p != node && p.Position.HasValue && HasTargets(p) && !UnderTargets(p)).Select(p => (Item: p, Pos: CurrentLocation.PlacePosition(p, thetas)));
            var best = NearestInBox(root.GlobalXf(StationIndex.DefaultRule, theta).Inverse(), box.Lo, box.Hi, CurrentLocation.PlacePosition(node, thetas), cands);
            return best == null ? null : new PlaceRedirect(best, null, $"{name} には行き先が無いので、近くの {best.Name} を開きました");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D (新しい方式)] 「{name}」の代わりに開く場所を決められませんでした (そのまま開きます): {ex.Message}");
            return null;
        }
    }
    // cands (全体の座標の位置) のうち、toLocal で場所の座標に直した点が箱 lo〜hi の中にあるもので、from (全体の座標) からいちばん近いもの。無ければ null
    internal static T? NearestInBox<T>(StationXf toLocal, double[] lo, double[] hi, (double X, double Y, double Z) from, IEnumerable<(T Item, (double X, double Y, double Z) Pos)> cands) where T : class
    {
        T? best = null; var bd = double.MaxValue;
        foreach (var (item, pos) in cands)
        {
            var q = toLocal.Apply(pos);
            if (q.X < lo[0] || q.X > hi[0] || q.Y < lo[1] || q.Y > hi[1] || q.Z < lo[2] || q.Z > hi[2]) continue;
            var d = (pos.X - from.X) * (pos.X - from.X) + (pos.Y - from.Y) * (pos.Y - from.Y) + (pos.Z - from.Z) * (pos.Z - from.Z);
            if (d < bd) { bd = d; best = item; }
        }
        return best;
    }
    private async Task HereAsync()
    {
        try
        {
            var status = "";
            if (CaptureHook is { } hook)
            {
                var (ok, st) = await hook("ステーションの 3D (新しい方式)");
                status = st;
                if (!ok) { Post(new { type = "here", ok = false, note = $"現在位置を取れませんでした: {st}" }); return; }
            }
            var r = await LocalOfCurrentAsync();
            if (r.Error != null) { Post(new { type = "here", ok = false, note = r.Error }); return; }
            var local = r.Pos!.Value; var note = r.Note;
            Post(new { type = "here", ok = true, pos = new[] { local.X, local.Y, local.Z }, utc = r.Utc, note = (status.Length > 0 ? status + " " : "") + note,
                hangar = r.Hangar is { } hs ? new { size = hs.Size, index = hs.Index, dist = Math.Round(hs.Dist) } : null });
            PostHereMark(r);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D (新しい方式)] 「{_title}」の現在位置を出せませんでした: {ex}");
            Post(new { type = "here", ok = false, note = $"現在位置を出せませんでした: {ex.Message}" });
        }
    }

    // 今の現在位置 (App.Config.MapCurrent) を、この場所の座標にする (前の HereAsync の直し方のまま)。Error が null でなければ出せない理由。Spins: 自転する場所 (自転の補正を当てた)
    private sealed record HereLocal((double X, double Y, double Z)? Pos, string Note, string? Error, bool Spins, double Utc, HangarHit? Hangar = null);
    private async Task<HereLocal> LocalOfCurrentAsync()
    {
            var cur = App.Config.MapCurrent;
            if (cur.Utc <= 0) return new HereLocal(null, "", "現在位置がまだありません (［マップ］タブの［現在位置］で取ってください)", false, 0);
            if (Ship3DExports.CurrentStamp() is not { } stamp) return new HereLocal(null, "", "ゲームの Data.p4k が見つかりません", false, cur.Utc);
            // 場所のルートを探すのは、場所と Data.p4k の印ごとに 1 回だけ (RootOfAsync。見つからなかったことも覚える。走っている最中に頼まれたら、その 1 本を待つ)
            _root = await RootOfAsync(_record, stamp);
            if (_root is not { } root) return new HereLocal(null, "", "ゲームデータの中にこの場所のルートが見つかりませんでした", false, cur.Utc);
            var fx = FixHere(root, _stationLayout, cur, await SameSystemAsync(_record, cur), out var err);
            if (fx == null) return new HereLocal(null, "", err ?? "現在位置を出せませんでした", root.Spins, cur.Utc);
            return new HereLocal(fx.Local, fx.Note, null, root.Spins, cur.Utc, fx.Hangar);
    }

    // ===== 現在位置の印 (2026-11-05、利用者の指示「3D でも現在位置が欲しい」「これでハンガーの中の位置が 3D で出ると最高」。親の決定 3・4) =====
    // ビューアへ {type:'hereMark', pos, utc, spin, note} (pos はこの場所の座標)。点がこの場所の外 (区画の置き方の一覧の箱のうち、一辺が HereMarkBoxMax m 未満の
    // どの箱も、HereMarkMargin m 広げた中に点が無い) なら {type:'hereMark', pos: null} (印を消す)。spin: 自転する場所 (位置が大きくずれることがある)。
    // (2026-11-05、検証の指摘: 合わせた 1 つの箱にすると Orison は遠くの区画のため 157 × 164 × 12,531 km になった。1 つずつの箱で見る)。
    // HereAsync が自転する場所で合わせる区画の箱 (HereBoxMax 500 m 未満) は、どれもこの箱 (5,000 m 未満・200 m 広げたもの) に入るので、HereAsync が区画の中に合わせた点には印も出る
    private const double HereMarkBoxMax = 5000, HereMarkMargin = 200;
    private void PostHereMark(HereLocal r)
    {
        if (_closed) return;
        if (r.Pos is not { } p) { Post(new { type = "hereMark", pos = (double[]?)null }); return; }
        // ハンガーの中と判定した点は、本体の側に印を出さない (知らせとボタンだけ。2026-11-05、親の決定)
        if (r.Hangar is { } hh) { Post(new { type = "hereMark", pos = (double[]?)null, at = new[] { p.X, p.Y, p.Z }, utc = r.Utc, spin = r.Spins, hangar = new { size = hh.Size, index = hh.Index, dist = Math.Round(hh.Dist) } }); return; }
        var boxes = (_stationLayout?.Instances ?? new()).Where(i => i.Lo != null && i.Hi != null && i.Hi[0] - i.Lo[0] < HereMarkBoxMax && i.Hi[1] - i.Lo[1] < HereMarkBoxMax && i.Hi[2] - i.Lo[2] < HereMarkBoxMax).ToList();
        bool inside = boxes.Any(i => p.X >= i.Lo![0] - HereMarkMargin && p.X <= i.Hi![0] + HereMarkMargin
            && p.Y >= i.Lo[1] - HereMarkMargin && p.Y <= i.Hi[1] + HereMarkMargin
            && p.Z >= i.Lo[2] - HereMarkMargin && p.Z <= i.Hi[2] + HereMarkMargin);
        // at: 直した点 (印を出さないときも、ハンガーの中のときの知らせ・ハンガーを開くボタンに使う)
        if (!inside) { Post(new { type = "hereMark", pos = (double[]?)null, at = new[] { p.X, p.Y, p.Z }, utc = r.Utc, hangar = r.Hangar is { } h0 ? new { size = h0.Size, index = h0.Index, dist = Math.Round(h0.Dist) } : null }); return; }
        Post(new { type = "hereMark", pos = new[] { p.X, p.Y, p.Z }, at = new[] { p.X, p.Y, p.Z }, utc = r.Utc, spin = r.Spins, note = r.Note,
            hangar = r.Hangar is { } hs ? new { size = hs.Size, index = hs.Index, dist = Math.Round(hs.Dist) } : null });
    }
    // 今の現在位置で印を置き直す (取り直さない)。ビューアの {type:'hereMarkGet'} (開いたとき) と、現在位置を取ったとき (NotifyCurrentChanged) から
    private async Task RefreshHereMarkAsync()
    {
        try { var r = await LocalOfCurrentAsync(); PostHereMark(r); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D (新しい方式)] 「{_title}」の現在位置の印を出せませんでした: {ex.Message}"); }
    }
    // ===== 現在位置を場所の座標に直す・ハンガーの中か (2026-11-05、利用者の指示「さっき、seraphim で現在位置を押してハンガーって出た」「再調査は時間の無駄」。移植) =====
    // v2.5.4 (4bfc288) の 2D の地図の処理の移植:
    //   補正値の選び方 = StationMapPanel.CorrectionsFor (下の CorrectionsFor。そのまま)。
    //   直し方の順 = StationMapPanel の現在位置 (UpdateCurrent): 自転する場所は CorrectionsFor の補正で取った時刻の角度を出し (無ければ出せない)、
    //     時刻合わせ (前は StationMapModel.FitStanding = 床の上になる δ、無ければハンガーの中になる δ) をし、local = 合わせた点 ?? 取った時刻の点、
    //     hangar = 合わせた結果のハンガー ?? (合わせられなかったとき HangarPointAt(local))。
    //   ハンガーの判定 = StationMapWeb.HangarAt・HangarPointAt (下の HangarAt・HangarPointAt。定数 HangarRadius・HangarDepth・HangarAbove はそのまま)。
    // 前と違う所 (データが無いため):
    //   ・時刻合わせの「床の上」は、v2.6.0 で床の形 (2D の地図の階の床の輪) が無くなったので、今の 3D の決まり (区画の箱 HereBoxMax 未満の中) のまま。
    //   ・HangarPointAt の OnAnyFloor (床の上か) と NearGraph (道順のグラフの節点から MappedNear 30 m 以内か)、FitStanding のハンガー探しの途中の NearGraph の打ち切りは、
    //     床の形・道順のグラフが今のデータに無いので見ない (2026-11-05、担当が決めた形。版 8 の DB の 6 か所で、乗り場・店・端末の点 1,503 個のどれも HangarAt に入らなかった)
    internal sealed record HangarHit(int Index, string Size, double Dist);
    internal sealed record HereFix((double X, double Y, double Z) Local, string Note, HangarHit? Hangar);
    // StationMapWeb.HangarRadius・HangarDepth・HangarAbove (v2.5.4 の値のまま)
    private const double HangarRadius = 150, HangarDepth = 150, HangarAbove = 0;
    // StationMapWeb.FitWindowSeconds・FitStepSeconds (v2.5.4 の値のまま。ハンガーの中になる時刻を探すのに使う)
    private const double OldFitWindowSeconds = 1.5, OldFitStepSeconds = 0.001;
    // StationMapWeb.HangarAt の移植: 局所の点が入るハンガーの範囲 (入口から水平に HangarRadius m 以内、高さが入口の HangarDepth m 下から HangarAbove m 上まで。
    // いくつも当たれば水平にいちばん近い入口)。入口は place_hangars (前の地図の JSON の exterior.hangars と同じく StationTree の exterior の hangar を木の順に。Index はその順の番号)。無ければ null
    internal static HangarHit? HangarAt((double X, double Y, double Z) p, StationView3D.Layout layout)
    {
        HangarHit? best = null; double bd = double.MaxValue;
        var hs = layout.Place?.Hangars ?? new();
        for (int i = 0; i < hs.Count; i++)
        {
            var h = hs[i].Pos;
            if (p.Z > h[2] + HangarAbove || p.Z < h[2] - HangarDepth) continue;
            var d = Math.Sqrt((p.X - h[0]) * (p.X - h[0]) + (p.Y - h[1]) * (p.Y - h[1]));
            if (d <= HangarRadius && d < bd) { bd = d; best = new HangarHit(i, hs[i].Size, d); }
        }
        return best;
    }
    // StationMapWeb.HangarPointAt の移植: Hangars.Count == 0 なら null、OnAnyFloor(p) || NearGraph(p, MappedNear) なら null、ほかは HangarAt。
    //   NearGraph (道順のグラフの節点から MappedNear m 以内) の代わり (2026-11-05、親の決定): 道順のグラフが今のデータに無いので、その場所の本体の中の分かっている点
    //   (区画の置き方の一覧の DB の、エレベーター・トラムの乗り場 (place_transit の elevator・elevator-noride・tram)・店 (place_shops)・端末 (place_terms) の位置) の
    //   どれかから MappedNear m 以内 (3D の距離) なら、ハンガーの中としない。
    //   OnAnyFloor (どれかの階の床の上か) の代わりは入れていない (床の形 (2D の地図の階の床の輪) が今のデータに無いため)
    private const double MappedNear = 30;   // StationMapWeb.MappedNear (v2.5.4 の値のまま)
    internal static HangarHit? HangarPointAt((double X, double Y, double Z) p, StationView3D.Layout layout)
    {
        if (layout.Place is not { } pl || pl.Hangars.Count == 0) return null;
        bool Near(double[] q) => (q[0] - p.X) * (q[0] - p.X) + (q[1] - p.Y) * (q[1] - p.Y) + (q[2] - p.Z) * (q[2] - p.Z) <= MappedNear * MappedNear;
        if (pl.Transit.Any(t => t.Kind is "elevator" or "elevator-noride" or "tram" && Near(t.Pos)) || pl.Shops.Any(s => Near(s.Pos)) || pl.Terms.Any(t => Near(t.Pos))) return null;
        return HangarAt(p, layout);
    }
    // StationMapPanel.CorrectionsFor の移植 (そのまま): 今の現在位置 (時刻 utc) で測り直した補正値 (CurrentLocation.Locate が「点 = 基準の場所の中心」とみなして測ったもの) があれば、
    // それは使わず、その前の補正値 (CurrentLocation.PreviousCorrections) に差し替える。前の値が無ければ、測り直した値をそのまま使い、note に知らせの文を入れる
    private static IReadOnlyList<MapSpinCorrection> CorrectionsFor(double utc, string body, out string note)
    {
        note = "";
        var list = App.Config.MapSpinCorrections;
        if (list.LastOrDefault(c => c.Body == body && double.IsFinite(c.OffsetDeg)) is not { } latest || latest.Utc != utc) return list;
        MapSpinCorrection? prev;
        lock (CurrentLocation.PreviousCorrections) CurrentLocation.PreviousCorrections.TryGetValue(body, out prev);
        if (prev == null || !double.IsFinite(prev.OffsetDeg))
        {
            note = "（前の自転の補正値が無いので、今回の点で測った補正値を使っています）";
            return list;
        }
        return list.Where(c => c.Body != body).Append(prev).ToList();
    }
    // 移植元 (StationMapPanel の現在位置) の sameSystem: 現在位置の星系 (CurrentLocation.Locate の System) が場所の星系 (木の場所の Root) と同じか。
    // 木・場所・星系が引けなければ true (移植元の res == null || node == null と同じ)。UI の糸から呼ぶ (［マップ］タブの木を引く)
    private static async Task<bool> SameSystemAsync(string record, MapCurrentState cur)
    {
        try
        {
            if (MapPanel.SharedTreeAsync() is not { } tt || await tt is not { } tree) return true;
            var node = tree.NodeByStarmapRecord(record);
            var res = CurrentLocation.Locate(tree, (cur.X, cur.Y, cur.Z), cur.Utc, MapPanel.SystemNames, null, fresh: false);
            return res == null || node == null || res.System == node.Root;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return true; }
    }
    // 今の現在位置 (cur) を場所 (root・layout) の座標に直し、ハンガーの中かも決める。直せなければ null (error に理由)。
    // 3D の［現在位置］・印・［マップ］タブの現在地の文のどれも、この同じ直し方を使う
    // sameSystem: 現在位置の星系がこの場所の星系か (移植元の sameSystem。違えば時刻合わせもハンガーの判定もしない。SameSystemAsync)
    internal static HereFix? FixHere(StationRoot root, StationView3D.Layout? layout, MapCurrentState cur, bool sameSystem, out string? error)
    {
        error = null;
        var global = (cur.X, cur.Y, cur.Z);
        if (!root.Spins)
        {
            var l0 = root.ToLocal(global);
            return new HereFix(l0, "", sameSystem && layout != null ? HangarPointAt(l0, layout) : null);
        }
        var body = BodyRecordOfUuid(root.SpinUuid);
        if (body == null) { error = "自転する天体が星図に見つからないので、現在位置を出せません"; return null; }
        var corr = CorrectionsFor(cur.Utc, body, out var corrNote);
        if (root.CorrectedTheta(cur.Utc, body, corr) is not { } th0) { error = "自転の角度が分からないので、現在位置を出せません"; return null; }
        var p0 = root.ToLocal(global, StationIndex.DefaultRule, th0);
        if (!sameSystem) return new HereFix(p0, corrNote, null);
        // (2026-11-05、親の決定) まず、取った時刻のままの点 (δ = 0) がハンガーの範囲に入るかを見る (移植元の第 1 段「床の上」は床の形の中だったが、今の第 1 段の区画の箱は
        // 床よりずっと広く、ハンガーの点が少しずらした所で区画の箱に入ってしまうため。2026-11-02 の RR_P3_LEO の点)
        if (layout != null && HangarPointAt(p0, layout) is { } h0) return new HereFix(p0, corrNote, h0);
        var boxes = (layout?.Instances ?? new()).Where(i => i.Lo != null && i.Hi != null && i.Hi[0] - i.Lo[0] < HereBoxMax && i.Hi[1] - i.Lo[1] < HereBoxMax && i.Hi[2] - i.Lo[2] < HereBoxMax).ToList();
        bool InBox((double X, double Y, double Z) q) => boxes.Any(i => q.X >= i.Lo![0] && q.X <= i.Hi![0] && q.Y >= i.Lo[1] && q.Y <= i.Hi[1] && q.Z >= i.Lo[2] && q.Z <= i.Hi[2]);
        // 時刻合わせ: まず区画の中 (今の 3D の決まりのまま: δ = 0 から HereFitStep 秒ずつ − ・ + へ)、無ければハンガーの中 (前の FitStanding のハンガー探し)
        IEnumerable<double> Deltas() { yield return 0; for (double k = HereFitStep; k <= HereFitSeconds + 1e-9; k += HereFitStep) { yield return -k; yield return k; } }
        foreach (var dt in Deltas())
        {
            if (root.CorrectedTheta(cur.Utc + dt, body, corr) is not { } th) continue;
            var q = root.ToLocal(global, StationIndex.DefaultRule, th);
            if (InBox(q)) return new HereFix(q, corrNote + (dt != 0 ? $"（取った時刻を {dt:+0.00;-0.00} 秒ずらして区画の中に合わせました）" : ""), null);
        }
        // ハンガーの中になる δ: 前の FitStanding のハンガー探しと同じ刻み (FitStepSeconds 0.001 秒・FitWindowSeconds 1.5 秒) と順 (+ を先に)
        IEnumerable<double> OldDeltas() { yield return 0; int n = (int)Math.Round(OldFitWindowSeconds / OldFitStepSeconds); for (int k = 1; k <= n; k++) { yield return k * OldFitStepSeconds; yield return -k * OldFitStepSeconds; } }
        if (layout != null && (layout.Place?.Hangars ?? new()).Count > 0)
            foreach (var dt in OldDeltas())
            {
                if (root.CorrectedTheta(cur.Utc + dt, body, corr) is not { } th) continue;
                var q = root.ToLocal(global, StationIndex.DefaultRule, th);
                if (HangarPointAt(q, layout) is { } h) return new HereFix(q, corrNote + (dt != 0 ? $"（取った時刻を {dt:+0.00;-0.00} 秒ずらしてハンガーの中に合わせました）" : ""), h);
            }
        return new HereFix(root.ToLocal(global, StationIndex.DefaultRule, th0), corrNote + "（区画の中に合わせられませんでした。取った時刻のままの位置です）", null);
    }
    // ［マップ］タブの現在地の文の「・ハンガー（S）」(MapPanel.NearInnerHtml)。今の現在位置 (cur) が場所 record のハンガーの所なら大きさ、そうでない・まだ求めていなければ null。
    // 求めていなければ裏で求め (ルート・区画の置き方の一覧を読む)、済んだら HangarNoteReady を UI スレッドで知らせる (［マップ］タブが書き直す)
    public static event Action? HangarNoteReady;
    private static (string Key, string? Size) s_mapHangar = ("", null);
    private static string s_mapHangarPending = "";
    public static string? MapHangarSize(string record, MapCurrentState cur)
    {
        if (cur.Utc <= 0 || record.Length == 0) return null;
        var key = record + "|" + cur.Utc.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        lock (s_views) { if (s_mapHangar.Key == key) return s_mapHangar.Size; if (s_mapHangarPending == key) return null; s_mapHangarPending = key; }
        var disp = System.Windows.Application.Current?.Dispatcher;
        var sameTask = SameSystemAsync(record, cur);   // ［マップ］タブの木は UI の糸から引く
        _ = Task.Run(async () =>
        {
            string? size = null;
            try
            {
                if (Ship3DExports.CurrentStamp() is { } stamp && await RootOfAsync(record, stamp) is { } root
                    && StationView3D.ReadLayoutDb(StationView3DLayout.DbPath, StationView3DLayout.GameKey(stamp), record) is { } lay)
                {
                    size = FixHere(root, lay, cur, await sameTask, out _)?.Hangar?.Size;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { App.AppendStartupLog(App.Config.WorkingDirectory, $"[マップ] 現在位置がハンガーの所かを求められませんでした: {ex.Message}"); }
            lock (s_views) { s_mapHangar = (key, size); if (s_mapHangarPending == key) s_mapHangarPending = ""; }
            if (size != null) disp?.BeginInvoke(() => HangarNoteReady?.Invoke());
        });
        return null;
    }
    // ［マップ］の現在位置を押したとき (MapPanel.GoHere3DAsync): この 3D で、3D の［現在位置］と同じ出し方 (取り直さない) で現在位置の所を出す。
    // ページがまだ用意できていなければ (station.json を読んで hereMarkGet を送る前)、用意できたときに出す
    private bool _hereRequested, _pageReady;
    public void RequestHere()
    {
        _hereRequested = true;
        _exteriorRequested = false;
        if (_pageReady && !_closed) _ = ShowHereNoCaptureAsync();
    }
    // ===== 外から見る形 (2026-11-06、利用者の指示「あとあると便利なのは、現在位置を取ったときにステーションから 50Km 以内かつそのステーションから出た後でなければ、
    //  そのステーションの外観の 3D を map 表示して pad の場所などを分かりやすくする機能」。親の決定) =====
    // ［マップ］の現在位置で、近くのステーションを外から見る形で出すとき (MapPanel.GoHere3DAsync): この 3D を、外から全体が見える形にする (中の行き先へは飛ばない)。
    // ビューアへ {type:'exteriorView'} を送る (view3d.js の onExteriorView。今ある外部視点 (外から見たときの印と見出し) になる所まで引くだけで、新しい描き方は無い)。
    // ページがまだ用意できていなければ (station.json を読んで hereMarkGet を送る前)、用意できたときに送る。後から頼まれたほう (RequestHere か、これ) だけを行う
    private bool _exteriorRequested;
    private (double X, double Y, double Z)? _exteriorFrom;
    // from: QT から出たときの、飛んできた所 (全体の座標。出発地、取れなければ最後に取った座標)。あれば、ステーションの中心からその向きへ離れた所から中心を向く (MapPanel の QT の到着)
    public void RequestExterior((double X, double Y, double Z)? from = null)
    {
        _exteriorRequested = true;
        _exteriorFrom = from;
        _hereRequested = false;
        if (_pageReady && !_closed) _ = PostExteriorViewAsync();
    }
    // (2026-11-06、利用者の指示「全体の80% を占める感じで画面の中心をステーションの中心にそろえて出してくれるとキレイ」
    //  「QT 時には目的地が station ならQT 宙に読み込んでおいて、自分の飛んでいく方向から中心を向って向いた外部からの視点で QT から離脱したときに出してほしい。」。親の決定)
    // ビューアへ {type:'exteriorView', lo, hi, at, dir} を送る (ExteriorPlanAsync。作れなければ箱なしの {type:'exteriorView'} = 前の決まり)
    private async Task PostExteriorViewAsync()
    {
        _exteriorRequested = false;
        var from = _exteriorFrom; _exteriorFrom = null;
        ExteriorPlan? plan = null;
        try { if (_stationLayout is { } lay) plan = await ExteriorPlanAsync(_record, lay, from, LayoutIdOf(_palette), _p4kKey); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D (新しい方式)] 「{_title}」の外から見る形の構図を決められませんでした: {ex.Message}"); }
        if (_closed) return;
        if (plan == null) Post(new { type = "exteriorView" });
        else Post(new { type = "exteriorView", lo = plan.Lo, hi = plan.Hi, at = plan.At, dir = plan.Dir });
    }

    // ===== 外から見る形の構図 (2026-11-06。親の決定) =====
    // Lo・Hi: ステーションの全体の箱 (場所の座標)。場所の全部の大きな物体 (StationView3DCache の FindBig。保存してあれば) の置かれた所の箱を合わせた範囲。
    //   無ければ、区画の置き方の一覧の箱 (LayoutDbBox と同じ一覧 = Layout.Instances の Lo・Hi) を合わせた範囲。ただし、どれかの辺が ExteriorBoxMax m 以上の箱は入れない
    //   (Orison の一覧の箱を全部合わせると 180 × 168 × 12,608,649 m になる (配る一覧の写しで確かめた)。大きな物体の x の幅 5,000 m 未満の決まりと同じ値)。
    // At: 読み込む地点の中心。区画の置き方の一覧の箱 (辺が ExteriorBoxMax m 未満) を合わせた範囲の中心にいちばん近い、区画の箱 (辺が ExteriorBoxMax m 未満のもの) の中の点 (区画の無い所へ移らない)。
    // Dir: from (全体の座標) があれば、場所の座標に直した点から全体の箱の中心への向きの逆 (中心からその点への向き。正規化)。
    //   直し方は RedirectAsync と同じ (RootOfAsync の GlobalXf の逆。自転する場所は今の時刻の自転の角度)。直せなければ null (ビューアは今の向きのまま)
    internal sealed record ExteriorPlan(double[] Lo, double[] Hi, double[] At, double[]? Dir);
    private const double ExteriorBoxMax = 5000;
    private static async Task<ExteriorPlan?> ExteriorPlanAsync(string record, StationView3D.Layout layout, (double X, double Y, double Z)? from, string layoutId, string p4kKey)
    {
        double[] lo = [double.MaxValue, double.MaxValue, double.MaxValue], hi = [double.MinValue, double.MinValue, double.MinValue];
        var any = false;
        void Add(double[] a, double[] b) { for (int i = 0; i < 3; i++) { lo[i] = Math.Min(lo[i], a[i]); hi[i] = Math.Max(hi[i], b[i]); } any = true; }
        var cache = StationView3DCache.Current;
        double s3; lock (s_seenLock) s3 = s_seenS3;
        if (cache.Enabled && cache.FindBig(record, p4kKey, layoutId, s3, out _, out var bkey) is { } hit)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(Path.Combine(hit.Dir, "region.json"));
                using var doc = JsonDocument.Parse(bytes);
                if (doc.RootElement.TryGetProperty("instances", out var ins) && ins.ValueKind == JsonValueKind.Array)
                    foreach (var row in ins.EnumerateArray())
                    {
                        if (row.GetArrayLength() < 5) continue;
                        var a = row[3].EnumerateArray().Select(x => x.GetDouble()).ToArray(); var b = row[4].EnumerateArray().Select(x => x.GetDouble()).ToArray();
                        if (a.Length == 3 && b.Length == 3) Add(a, b);
                    }
            }
            catch (Exception e) when (e is IOException or JsonException or InvalidOperationException) { }
            finally { cache.Release(bkey); }
        }
        var small = layout.Instances.Where(i => i.Lo is { Length: 3 } && i.Hi is { Length: 3 } && i.Hi[0] - i.Lo[0] < ExteriorBoxMax && i.Hi[1] - i.Lo[1] < ExteriorBoxMax && i.Hi[2] - i.Lo[2] < ExteriorBoxMax).ToList();
        // 区画の置き方の一覧の箱 (辺が ExteriorBoxMax 未満のもの) を合わせた範囲の中心。読み込む地点 (At) はこれで決める (大きな物体の有無で変わらないので、QT の先読みで保存した地点がそのまま使える)
        double[] llo = [double.MaxValue, double.MaxValue, double.MaxValue], lhi = [double.MinValue, double.MinValue, double.MinValue];
        foreach (var i in small) for (int k = 0; k < 3; k++) { llo[k] = Math.Min(llo[k], i.Lo![k]); lhi[k] = Math.Max(lhi[k], i.Hi![k]); }
        if (!any) foreach (var i in small) Add(i.Lo!, i.Hi!);
        if (!any) return null;
        var c = new[] { (lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, (lo[2] + hi[2]) / 2 };
        var lc = small.Count > 0 ? new[] { (llo[0] + lhi[0]) / 2, (llo[1] + lhi[1]) / 2, (llo[2] + lhi[2]) / 2 } : c;
        double[] at = lc; var bd = double.MaxValue;
        foreach (var i in small)
        {
            var p = new[] { Math.Clamp(lc[0], i.Lo![0], i.Hi![0]), Math.Clamp(lc[1], i.Lo[1], i.Hi[1]), Math.Clamp(lc[2], i.Lo[2], i.Hi[2]) };
            var d = (p[0] - lc[0]) * (p[0] - lc[0]) + (p[1] - lc[1]) * (p[1] - lc[1]) + (p[2] - lc[2]) * (p[2] - lc[2]);
            if (d < bd) { bd = d; at = p; }
        }
        double[]? dir = null;
        if (from is { } f && Ship3DExports.CurrentStamp() is { } stamp && await RootOfAsync(record, stamp) is { } root)
        {
            var unix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            double? theta = 0;
            if (root.Spins) theta = BodyRecordOfUuid(root.SpinUuid) is { } body ? root.CorrectedTheta(unix, body, App.Config.MapSpinCorrections) : null;
            if (theta is { } th)
            {
                var q = root.GlobalXf(StationIndex.DefaultRule, th).Inverse().Apply(f);
                var v = new[] { q.X - c[0], q.Y - c[1], q.Z - c[2] };
                var l = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
                if (l > 1e-6) dir = [v[0] / l, v[1] / l, v[2] / l];
            }
        }
        return new ExteriorPlan(lo, hi, at, dir);
    }

    // ===== QT の目的地の先読み (2026-11-06、利用者の指示「QT 時には目的地が station ならQT 宙に読み込んでおいて」。親の決定) =====
    // MapPanel が QT の移動中に呼ぶ。表示は変えない。その場所の 3D を外から見る形で開いたときに頼む物を、裏で作って保存する (StationView3DCache):
    //   地点: 外から見る形の読み込む地点の中心 (ExteriorPlanAsync の At) で、ビューアが使った半径 (s_seenRadii。まだ無ければ 30・300 m) ごと (先読み (PrefetchAsync) と同じ作り方・保存の仕方)。
    //   場所の全部の大きな物体: ビューアが使った境 (s_seenS3。まだ無ければ 20 m)。
    // 色付けはビューアが最後に頼んだもの (s_seenPal)。同じ場所は同時に 1 本だけ。場所の中身の一覧 (DB) がまだ今の Data.p4k の物でなければしない。
    // 区画の取り出しは 1 本ずつ (CPU が高い間は待つ)。保存しない設定 (上限 0) ならしない。かかった時間はログに 1 行出す
    private static readonly object s_seenLock = new();
    private static readonly SortedSet<double> s_seenRadii = new();
    private static double s_seenS3 = 20;
    private static bool s_seenPal = true;
    private static readonly HashSet<string> s_prefetching = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim s_prefetchGate = new(1, 1);
    public static async Task PrefetchArrivalAsync(string record, string name, CancellationToken ct = default)
    {
        lock (s_prefetching) if (!s_prefetching.Add(record)) return;
        var cache = StationView3DCache.Current;
        Interlocked.Increment(ref s_open);
        try
        {
            if (!cache.Enabled || Ship3DExports.CurrentStamp() is not { } stamp) return;
            List<double> radii; double s3; bool palette;
            lock (s_seenLock) { radii = s_seenRadii.Count > 0 ? s_seenRadii.ToList() : [30, 300]; s3 = s_seenS3; palette = s_seenPal; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var built = await Task.Run(async () =>
            {
                var db = StationView3DLayout.DbPath;
                if (StationView3D.ReadLayoutDb(db, StationView3DLayout.GameKey(stamp), record) is not { } layout) return -1;
                var layoutId = palette ? LayoutIdNow() : LayoutIdNow() + StationView3DCache.NoPaletteSuffix;
                var plan = await ExteriorPlanAsync(record, layout, null, layoutId, stamp.Key);
                if (plan == null) return -1;
                var ini = Path.Combine(Path.GetDirectoryName(stamp.Path) ?? "", "data", "Localization", "japanese_(japan)", "global.ini");
                var loc = new StationView3D.Loc(ini, Path.Combine(DetailPanel.WorkDir(), "translations.db"));
                var session = new StationView3D.Session(layout, "", loc, terminalLabel: StationTerminalKinds.Label, texUrlBase: TexUrlBase);
                var extract = StationView3D.OwnExtractor(stamp.Path, m => App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D (新しい方式)] 「{name}」 (QT の先読み) {m}"),
                    texMaxPx: TexMaxPx, texFolder: Path.Combine(Ship3DExports.Folder, StationView3DTextures.FolderName), palette: palette);
                async Task<IDisposable> Acquire(CancellationToken t) { while (Ship3DExports.CpuHighNow) await Task.Delay(500, t); await s_prefetchGate.WaitAsync(t); return new Release(() => s_prefetchGate.Release()); }
                int n = 0;
                foreach (var r in radii)
                {
                    ct.ThrowIfCancellationRequested();
                    var key = StationView3DCache.KeyOf(record, stamp.Key, layoutId, plan.At, r);
                    if (cache.TryGet(key, record, stamp.Key, layoutId, plan.At, r) is { }) { cache.Release(key); continue; }
                    if (!cache.CanStore(PrefetchEstimateBytes)) break;
                    var stage = cache.BeginStage(key);
                    var st = await session.BuildRegionAsync(plan.At, r, stage, extract, Acquire, null, ct, palette);
                    if (cache.Commit(stage, key, record, stamp.Key, layoutId, plan.At, r, st.Socpaks, st.Missing) is { }) cache.Release(key); else StationView3DCache.TryDelete(stage);
                    n++;
                }
                if (cache.FindBig(record, stamp.Key, layoutId, s3, out _, out var bk) is { }) cache.Release(bk);
                else
                {
                    var bkey = StationView3DCache.KeyOfBig(record, stamp.Key, layoutId, s3);
                    if (cache.CanStore(PrefetchEstimateBytes))
                    {
                        var stage = cache.BeginStage(bkey);
                        var st = await session.BuildBigAsync(s3, stage, extract, Acquire, _ => Task.CompletedTask, ct);
                        if (cache.Commit(stage, bkey, record + StationView3DCache.BigSuffix, stamp.Key, layoutId, [s3], 0, st.Socpaks, 0) is { }) cache.Release(bkey); else StationView3DCache.TryDelete(stage);
                        n++;
                    }
                }
                return n;
            }, ct);
            if (built > 0) App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D (新しい方式)] 「{name}」を QT の間に先読みして保存しました ({built} 件・{sw.Elapsed.TotalSeconds:F1} 秒)");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D (新しい方式)] 「{name}」の QT の先読みを止めました: {ex.Message}"); }
        finally
        {
            lock (s_prefetching) s_prefetching.Remove(record);
            if (Interlocked.Decrement(ref s_open) <= 0) { s_open = 0; SocpakExporter.ReleaseShared(); }
        }
    }
    private async Task ShowHereNoCaptureAsync()
    {
        _hereRequested = false;
        try
        {
            var r = await LocalOfCurrentAsync();
            if (r.Error != null) { Post(new { type = "here", ok = false, note = r.Error }); return; }
            var local = r.Pos!.Value;
            Post(new { type = "here", ok = true, pos = new[] { local.X, local.Y, local.Z }, utc = r.Utc, note = r.Note,
                hangar = r.Hangar is { } hs ? new { size = hs.Size, index = hs.Index, dist = Math.Round(hs.Dist) } : null });
            PostHereMark(r);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D (新しい方式)] 「{_title}」の現在位置を出せませんでした: {ex}");
            Post(new { type = "here", ok = false, note = $"現在位置を出せませんでした: {ex.Message}" });
        }
    }
    // 開いている 3D (別ウィンドウ・［マップ］タブ) の一覧
    private static readonly HashSet<StationView3DView> s_views = new();
    // 現在位置を取った (［マップ］タブのボタン・自動の取得・3D の［現在位置］)。開いている 3D の印を置き直す (見ている所・歩くモードは動かさない)。どのスレッドから呼んでもよい (3D の UI スレッドで置き直す)
    public static void NotifyCurrentChanged()
    {
        List<StationView3DView> list; lock (s_views) list = s_views.ToList();
        foreach (var v in list) if (!v._closed && v._started) _ = v.Dispatcher.InvokeAsync(() => v.RefreshHereMarkAsync());
    }


    // 自転する天体の uuid → 星図の record 名 (gamedata_cache.db の starmap_locations を読み取り専用で引く)。引けなければ null
    private static string? BodyRecordOfUuid(string uuid)
    {
        try
        {
            var db = DetailPanel.DbPath();
            if (!File.Exists(db)) return null;
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT record_name FROM starmap_locations WHERE lower(uuid) = $u LIMIT 1";
            cmd.Parameters.AddWithValue("$u", uuid.ToLowerInvariant());
            return cmd.ExecuteScalar() as string;
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { return null; }
    }
    private StationView3D.Layout? _stationLayout;
    // 外から見たときの印 (StationView3D.ExteriorMarks の結果。StartAsync で裏に作り始め、ビューアの exteriorGet で返す)
    private Task<object>? _exterior;

    // 区画の置き方の一覧と場所の中身 (view3d_layout.db。StationView3DLayout が裏で一括に作る)。裏の糸から呼ぶ。
    // DB がまだ今の Data.p4k の印でなければ (裏で作っている途中・まだ始まっていない)、裏の一括を始めて (動いていればそれに合流して) 終わるまで待つ。
    // 待つ間は画面の真ん中に大きく「準備中…（済んだ数 / 全部）」を出す。ハンガーの中 (StationHangars の record) は一括に入らないので、無ければその 1 か所を作って DB に足す
    private StationView3D.Layout LayoutFor(string record, Ship3DExports.P4kStamp stamp, CancellationToken ct)
    {
        var db = StationView3DLayout.DbPath;
        var key = StationView3DLayout.GameKey(stamp);
        if (StationView3D.ReadLayoutDb(db, key, record) is { } hit) return hit;
        if (!StationView3D.LayoutDbIsCurrent(db, key))
        {
            var task = StationView3DLayout.Start("3D の窓を開いた");
            while (!task.Wait(500, ct))
            {
                var (d, n) = StationView3DLayout.Progress;
                Dispatcher.BeginInvoke(() => Overlay($"準備中…\n区画の置き方の一覧と場所の中身を、全部の場所の分まとめて作っています ({d} / {n} か所)"));
            }
            if (StationView3D.ReadLayoutDb(db, key, record) is { } hit2) return hit2;
        }
        if (!StationHangars.IsRecord(record)) throw new InvalidOperationException("ゲームデータの中にこの場所が見つかりませんでした" + (StationView3DLayout.LastError is { } e ? $" ({e})" : ""));
        Dispatcher.BeginInvoke(() => Overlay("準備中…\nハンガーの中の区画の置き方の一覧を作っています"));
        lock (StationView3DLayout.PutLock)
        {
            using var index = StationIndex.Build(stamp.Path, ct);
            var layout = StationView3D.LayoutOf(index, StationTree.Load(index, StationHangars.RootOf(record), contents: true, ct: ct));
            StationView3D.PutLayoutDb(db, key, record, layout, index.P4kPath);   // 場所のパレットの色・材質の差し替え (look_*) も、まだ無い socpak の分を足す (2026-11-05)
            return layout;
        }
    }

    // 取り出しを 1 本始めてよくなるまで待つ (クラスの説明)。返した IDisposable で返す
    private async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var got = await Dispatcher.InvokeAsync(() =>
            {
                if (!_ownInUse && !Ship3DExports.CpuHighNow) { _ownInUse = true; return 1; }
                if (_ownInUse && Ship3DExports.Instance.TryAcquireExtraRun()) return 2;
                return 0;
            });
            if (got == 1) return new Release(() => Dispatcher.BeginInvoke(() => _ownInUse = false));
            if (got == 2) return new Release(() => Dispatcher.BeginInvoke(() => Ship3DExports.Instance.ReleaseExtraRun()));
            await Task.Delay(500, ct);
        }
    }

    private sealed class Release(Action a) : IDisposable
    {
        private Action? _a = a;
        public void Dispose() { Interlocked.Exchange(ref _a, null)?.Invoke(); }
    }

    private void DeleteTemp()
    {
        if (_tempDir.Length == 0) return;
        var d = _tempDir;
        // 取り出し中の処理が終わるのを少し待ってから消す (20 秒たっても消せなければ残る)
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 20; i++)
            {
                try { if (Directory.Exists(d)) Directory.Delete(d, true); return; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Task.Delay(1000); }
            }
        });
    }
}

// 区画の置き方の一覧と場所の中身の DB (view3d_layout.db) を裏で一括に作る (2026-11-04、利用者の決定「裏でやっとく」)。
//   いつ: アプリの起動の後 (Ship3DExports の自動の取り直しと同じ時: 起動の後 AutoRefreshDelay たってから) と、Data.p4k の印が変わったとき。
//   DB が今の印で作ったものなら何もしない。動いている間に呼ばれたら、動いているものを返す (同時に 1 本だけ)。
//   作り終える前に 3D の窓を開いたときは、窓がこれに合流して、終わるまで「準備中…（済んだ数 / 全部）」を出して待つ (StationView3DWindow.LayoutFor)。
//   作るもの: 星図の場所の表 (ゲームデータの DB の starmap_locations) をルート探しして見つかった場所の全部 (StationView3D.BuildLayoutDb)
//   同梱 (2026-11-04、利用者の指示「DBに入れたものは配れる。時価データでなくアドレスだから」): インストール先 (AppContext.BaseDirectory) に view3d_layout.db があり、
//   その印が今のゲームの版と同じなら、作らずに置き場所へ写す (起動の後・窓を開いたとき)。印が違えば (パッチが来た) 裏で作り直す。
//   (2026-11-05) 置き場所の DB が今のゲームの版のときも、同梱の物が今の版で、作った時刻 (built_utc) が新しければ写す (CopyNewerBundled。3D が開いていない間だけ)
//   印 (GameKey) は、どの PC でも同じになるよう、Data.p4k と同じフォルダの build_manifest.id の Branch・Version・RequestedP4ChangeNum にする
//   (読めなければ、その PC の Data.p4k の印 (パス・大きさ・更新日時)。その DB は配っても使われない)
internal static class StationView3DLayout
{
    public static string DbPath => Path.Combine(StationPlaces.Folder, StationView3D.LayoutDbName);
    // 一括で並べる最大の本数 (試しの道具で全部の場所を作って計った値: 1 本 347 秒・メモリのピーク 3.5 GB、4 本 355 秒・3.1 GB。門で待つので速くはならないが、メモリのピークは下がった)
    public const int LayoutParallelMax = 4;
    public static string BundledPath => Path.Combine(AppContext.BaseDirectory, StationView3D.LayoutDbName);

    // 前の版の 2D の地図と古い方式のステーションの 3D のファイルを消す (2026-11-04、利用者の指示「2D 地図は消して」「他のユーザーも更新時に消す仕組みで」)。
    // いつ: アプリの起動の後 (MainWindow が裏で呼ぶ)。置き場所は設定の 3D の置き場所 (Ship3DExports.Folder)。
    // 消すもの (ファイルの名前の決まりで見分ける):
    //   stations の直下の *.json (2D の地図)・*.meshes.gz・*.meshes.keys (2D の地図の読み取りの保存)・*.layout.json (前の区画の置き方の一覧)。view3d_layout.db* は消さない
    //   stations\hangars の *.json・*.meshes.gz・*.meshes.keys (ハンガーの中の 2D の地図)。ハンガーの表 hangar_catalog.json は 3D で使うので消さない
    //   stations3d フォルダ (古い方式のステーションの 3D) を丸ごと
    // 全部消せたら、作業ディレクトリに印のファイル (OldMapsCleanedMark) を書いて、次からは消しに行かない。消せなかったもの (使用中など) があれば印を書かず、次の起動でまた試す
    public const string OldMapsCleanedMark = "old_station_maps_removed.txt";
    public static void CleanupOldMaps()
    {
        var mark = Path.Combine(App.Config.WorkingDirectory, OldMapsCleanedMark);
        try { if (File.Exists(mark)) return; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        int removed = 0, failed = 0; long bytes = 0;
        var names = new List<string>();
        void Del(FileInfo f)
        {
            try { var len = f.Length; f.Delete(); removed++; bytes += len; if (names.Count < 20) names.Add(f.Name); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed++; Log($"前の版の地図のファイルを消せませんでした (次の起動でまた試します): {f.FullName}: {ex.Message}"); }
        }
        static bool Is2DFile(string name) => name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".meshes.gz", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".meshes.keys", StringComparison.OrdinalIgnoreCase);
        try
        {
            var st = new DirectoryInfo(StationPlaces.Folder);
            if (st.Exists)
                foreach (var f in st.GetFiles())
                    if (Is2DFile(f.Name)) Del(f);
            var hg = new DirectoryInfo(StationPlaces.HangarFolder);
            if (hg.Exists)
                foreach (var f in hg.GetFiles())
                    if (Is2DFile(f.Name) && !f.Name.Equals(StationHangarCatalog.FileName, StringComparison.OrdinalIgnoreCase)) Del(f);
            var old3d = new DirectoryInfo(Path.Combine(Ship3DExports.Folder, "stations3d"));
            if (old3d.Exists)
            {
                foreach (var f in old3d.GetFiles("*", SearchOption.AllDirectories)) Del(f);
                if (failed == 0) try { old3d.Delete(true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed++; Log($"古い方式のステーションの 3D のフォルダを消せませんでした: {ex.Message}"); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed++; Log($"前の版の地図のファイルを消せませんでした: {ex.Message}"); }
        if (removed > 0) Log($"前の版の 2D の地図・古い方式のステーションの 3D のファイルを {removed} 個 ({bytes / 1048576.0:F1} MB) 消しました: {string.Join(", ", names)}{(removed > names.Count ? " ほか" : "")}");
        if (failed == 0)
            try { File.WriteAllText(mark, $"{DateTime.Now:o} 消したファイル {removed} 個 ({bytes} バイト)\n"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log($"消した印を書けませんでした (次の起動でもう一度見ます): {ex.Message}"); }
    }
    public static string GameKey(Ship3DExports.P4kStamp stamp)
    {
        var dir = Path.GetDirectoryName(stamp.Path) ?? "";
        var (version, change, branch) = GameDataExtractor.ReadBuildManifestFile(Path.Combine(dir, "build_manifest.id"));
        return version != null && change != null ? $"build|{branch}|{version}|{change}" : "p4k|" + stamp.Key;
    }
    // 同梱の DB が今の版のものなら、置き場所へ写す (写したら true)
    private static bool CopyBundled(string key)
    {
        try
        {
            if (!File.Exists(BundledPath) || !StationView3D.LayoutDbIsCurrent(BundledPath, key)) return false;
            Directory.CreateDirectory(StationPlaces.Folder);
            var tmp = DbPath + ".copy";
            File.Copy(BundledPath, tmp, true);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Move(tmp, DbPath, true);
            Log($"同梱の区画の置き方の一覧 (版 {key}) を置き場所へ写しました");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log($"同梱の区画の置き方の一覧を写せませんでした: {ex.Message}"); return false; }
    }
    // 置き場所の DB が今の版のとき (2026-11-05、親の決定): 同梱の DB も今の版で、同梱の作った時刻 (meta の built_utc。一括で作ったときだけ書かれる) が
    // 置き場所の DB より新しければ、同梱の物を置き場所へ写す (写したら true。決まりは StationLayoutBundled)。
    // DB だけの更新やアプリの更新で、同じパッチのまま配置を作り直して配ったものを入れるため。利用者が後から作り直した DB (時刻が新しい) は替えない。
    // ステーションの 3D が開いている間は写さない (開いている画面は前の DB の中身と保存の鍵で動いているため。次の起動の後にまた見る)。
    // 写すと、この PC で足したハンガーの中の分 (PutLayoutDb の行) は無くなり、次にそのハンガーを開いたときに作り直される (LayoutFor)
    private static bool CopyNewerBundled(string key)
    {
        try
        {
            if (!StationLayoutBundled.IsNewer(DbPath, BundledPath, StationView3D.LayoutDbFormat, key)) return false;
            if (StationView3DView.AnyOpen)
            {
                Log("同梱の区画の置き方の一覧のほうが新しいですが、ステーションの 3D を開いているので、今は写しません (次の起動の後に写します)");
                return false;
            }
            var tmp = DbPath + ".copy";
            File.Copy(BundledPath, tmp, true);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Move(tmp, DbPath, true);
            Log($"同梱の区画の置き方の一覧のほうが新しいので、置き場所へ写しました (版 {key}。ハンガーの中の分は、次に開いたときに作り直します)");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log($"同梱の新しい区画の置き方の一覧を写せませんでした (次の起動の後にまた試します): {ex.Message}"); return false; }
    }
    public static readonly object PutLock = new();
    private static readonly object Gate = new();
    private static Task? _task;
    private static string _taskKey = "";
    private static int _done, _total;
    public static (int Done, int Total) Progress => (Volatile.Read(ref _done), Volatile.Read(ref _total));
    public static string? LastError { get; private set; }
    private static void Log(string m) => App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D (新しい方式)] {m}");

    // 動いているか (設定タブの［マップをまとめて取り込む］の表示に使う)。Changed は始めた・済んだときに (どの糸からでも) 呼ぶ
    public static bool Running { get { lock (Gate) return _task != null && !_task.IsCompleted; } }
    public static event Action? Changed;
    public static bool IsCurrent() => Ship3DExports.CurrentStamp() is { } st && StationView3D.LayoutDbIsCurrent(DbPath, GameKey(st));
    // DB の問題 (2026-11-04、利用者の指示「問題あれば有効、なければ無効」。設定タブの［マップをまとめて取り込む］の有効・無効と横の文)。問題が無ければ null。
    // 問題: Data.p4k が見つからない・DB が無い・今の Data.p4k の版で作っていない・作れなかった場所がある・作りかけ (.tmp) が残っている
    public static string? Problem()
    {
        if (Ship3DExports.CurrentStamp() is not { } st) return "ゲームの Data.p4k が見つかりません";
        if (!File.Exists(DbPath)) return File.Exists(DbPath + ".tmp") ? "作りかけで止まっています" : "まだ作っていません";
        if (!StationView3D.LayoutDbIsCurrent(DbPath, GameKey(st))) return "今のパッチの版で作っていません";
        if (int.TryParse(StationView3D.LayoutDbMeta(DbPath, "failed"), out var f) && f > 0) return $"作れなかった場所が {f} か所あります";
        if (File.Exists(DbPath + ".tmp") && !Running) return "作りかけで止まっています";
        return null;
    }

    // force: 今の版の DB があっても作り直す (設定タブの［マップをまとめて取り込む］)
    public static Task Start(string why, bool force = false)
    {
        if (Ship3DExports.CurrentStamp() is not { } stamp) return Task.CompletedTask;
        var key = GameKey(stamp);
        lock (Gate)
        {
            if (_task != null && !_task.IsCompleted && _taskKey == key) return _task;
            if (!force)
            {
                if (StationView3D.LayoutDbIsCurrent(DbPath, key))
                {
                    // 置き場所の DB は今の版。同梱の物のほうが新しく作ったものなら写す (CopyNewerBundled)。写したら、作り直したときと同じく Changed を知らせる
                    bool copiedNewer;
                    lock (PutLock) copiedNewer = CopyNewerBundled(key);
                    if (copiedNewer) Task.Run(() => Changed?.Invoke());
                    return Task.CompletedTask;
                }
                lock (PutLock) if (CopyBundled(key)) return Task.CompletedTask;
            }
            _taskKey = key; _done = 0; _total = 0; LastError = null;
            var job = NewJob();
            _task = Task.Factory.StartNew(() => Build(stamp, why, force, job), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        Changed?.Invoke();
        return _task;
    }

    // 進み具合の欄 (Ship3DProgressPanel。船の書き出しと同じ欄) に出す 1 行 (2026-11-04、利用者の指摘「バーが出ない」)。取り消しのボタンで止められる (job.Cts)。
    // ％と残り (2026-11-04、利用者の指摘「予想が11分まで伸びた。予想とは？」で、場所の数でなく重さで見るように直した):
    //   予想の総時間 (EstimatedSeconds) を、済んだ重さの割合 f (BuildLayoutDb の weightProgress。場所ごとの前の秒、無ければ置かれた所の数から見込んだ重さ) が
    //   WeightMin 未満の間は前に作ったときの秒 (DB の meta の seconds。無ければ FirstGuessSeconds)、その後は「経過 ÷ f」にして、Ship3DJob の「経過 ÷ (経過 + 残り)」で出す。
    //   重い場所から始めるので、最後に長い場所が残って予想が伸び続けることが少なくなる
    private const double FirstGuessSeconds = 350;
    private static Ship3DJob? NewJob()
    {
        var disp = Application.Current?.Dispatcher;
        if (disp == null) return null;
        var job = new Ship3DJob("view3d_layout", "view3d-layout", "区画の置き方の一覧", null) { Kind = Ship3DJobKind.Station, LogTag = "区画の置き方の一覧" };
        job.EstimatedSeconds = PreviousSeconds() ?? FirstGuessSeconds;
        disp.Invoke(() => { Ship3DExports.Instance.AddJob(job); job.Watch.Start(); job.SetPhase(Ship3DJobPhase.Exporting, "区画の置き方の一覧を作っています"); });
        return job;
    }
    // 前に作ったときにかかった秒 (DB の meta の seconds)。無ければ null
    private static double? PreviousSeconds()
    {
        try
        {
            if (!File.Exists(DbPath)) return null;
            using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath};Mode=ReadOnly;Pooling=False");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key = 'seconds'";
            return double.TryParse(cmd.ExecuteScalar() as string, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { return null; }
    }
    private const double WeightMin = 0.02;
    private static void JobProgress(Ship3DJob? job, double f)
    {
        if (job == null) return;
        var e = job.Watch.Elapsed.TotalSeconds;
        if (f >= WeightMin) job.EstimatedSeconds = e / f;
        Application.Current?.Dispatcher.BeginInvoke(job.Refresh);
    }
    private static void JobEnd(Ship3DJob? job, Ship3DJobPhase phase)
    {
        if (job == null) return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            job.Watch.Stop();
            job.SetPhase(phase, "");
            Ship3DExports.Instance.RemoveJob(job);
        });
    }

    private static void Build(Ship3DExports.P4kStamp stamp, string why, bool force, Ship3DJob? job)
    {
        var ct = job?.Cts.Token ?? CancellationToken.None;
        var phase = Ship3DJobPhase.Done;
        try
        {
            lock (PutLock)
            {
                var key = GameKey(stamp);
                if (!force && StationView3D.LayoutDbIsCurrent(DbPath, key)) return;
                Log($"区画の置き方の一覧と場所の中身を裏で一括に作ります ({why})");
                var places = StationPlaces.LoadPlaces(DetailPanel.DbPath()).Select(p => p.Place).ToList();
                using var index = StationIndex.Build(stamp.Path);
                var roots = index.FindRoots(places).Roots;
                Directory.CreateDirectory(StationPlaces.Folder);
                // 並べる数: 船の書き出しと同じ決まり (Ship3DExports.MaxParallelExports: CPU の数の半分と空きメモリ ÷ 3 GB の小さいほう) で、最大 LayoutParallelMax 本。
                // メモリは BuildLayoutDb の門 (StationView3D.LayoutMemGate) で 4 GB 以下に抑える (親の決定)。1 か所を始める前に、CPU 使用率が 90% 以上なら下がるまで待つ
                var par = Math.Min(LayoutParallelMax, Ship3DExports.MaxParallelExports().Limit);
                var res = StationView3D.BuildLayoutDb(DbPath, key, index, roots, (d, n) => { Volatile.Write(ref _done, d); Volatile.Write(ref _total, n); }, ct, par,
                    c => { while (Ship3DExports.CpuHighNow) Task.Delay(1000, c).Wait(c); }, f => JobProgress(job, f),
                    fillLog: m => App.AppendStartupLog(App.Config.WorkingDirectory, $"[床の補い] {m}"),
                    roofLog: m => App.AppendStartupLog(App.Config.WorkingDirectory, $"[屋根の扉] {m}"));
                Log($"並べた数 {par}");
                Log($"一括で作りました: 場所 {res.Places}・置かれた所 {res.Instances}・socpak {res.Socpaks}・{res.Bytes / 1024} KB・{res.Seconds:F1} 秒" +
                    (res.Failed.Count > 0 ? $"。作れなかった場所 {res.Failed.Count}: {string.Join(" / ", res.Failed)}" : ""));
            }
        }
        catch (OperationCanceledException)
        {
            phase = Ship3DJobPhase.Cancelled;
            LastError = "取り消しました";
            Log("一括で作るのを取り消しました (前の DB はそのまま)");
            try { File.Delete(DbPath + ".tmp"); } catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException) { }
        }
        catch (AggregateException ae) when (ae.InnerExceptions.All(x => x is OperationCanceledException))
        {
            phase = Ship3DJobPhase.Cancelled;
            LastError = "取り消しました";
            Log("一括で作るのを取り消しました (前の DB はそのまま)");
            try { File.Delete(DbPath + ".tmp"); } catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception ex)
        {
            phase = Ship3DJobPhase.Failed;
            LastError = ex.Message;
            Log($"一括で作れませんでした: {ex}");
        }
        finally { JobEnd(job, phase); _ = Task.Run(() => Changed?.Invoke()); }
    }
}
