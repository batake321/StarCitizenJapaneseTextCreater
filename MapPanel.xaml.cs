using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Microsoft.Data.Sqlite;
using Microsoft.Web.WebView2.Core;

namespace StarCitizenJapaneseTextCreater;

// ［マップ］タブ。Stanton・Pyro・Nyx の星系図 (GameDataQueryService.SystemMapSvg。詳細タブと同じ図) を WebView2 で出す。
//   ページは詳細タブと同じ作り (DetailHtml.Page: 同じ CSS・CSP・図を動かすスクリプト、DetailHtml.MapBlock: 図と［全体］［＋］［－］)。
//   ［3 星系］: 3 つの星系図を横に並べる (狭ければ折り返す)。［Stanton］［Pyro］［Nyx］: その星系図 1 つを枠いっぱいに出す。
//   最後の表示 (3 星系 / 星系) と、星系図ごとに最後に操作した表示 (中心・幅) を覚えて (App.Config.MapView / MapViews)、次に開いたときもその表示で出す。
//     図の操作が止まると、図のスクリプトが window.chrome.webview.postMessage で星系・中心・幅を送ってくる (svg の data-save。マップタブの図だけ)。
//   図の点・ラベルを押すと (scjt://detail/… のリンク)、DetailPanel.Request で［詳細］タブにその場所の詳細を開く。
//   データは詳細タブと同じ ShopTree (GameDataQueryService.BuildShopTree)。表が作り直されたら (GameDataExtractor.ShopTablesChanged) 作り直す。
//   ［現在位置］: ゲームに /showlocation を送って座標を読み (ShowLocationSender)、現在位置の星系・近くの場所を求め、近くの惑星の自転の角度を実測して
//     補正値を覚え (CurrentLocation)、その星系 1 つの表示にして印と【現在位置の近く】を出す。最後の現在位置と補正値は覚える (App.Config.MapCurrent / MapSpinCorrections)。
//     押したとき、今わかっている現在位置の星系が今の表示と違えば、取る前にその星系 1 つの表示に切り替える (取れなくても切り替わる)。
//   図の惑星・衛星の自転の角度は、現在位置があればその取得時刻、無ければ今の時刻のもの
//   タブを初めて開いたとき (Loaded) に作る
//   星系図と場所の階層マップ (2026-11-01、利用者の指示「マップも星系と場所の地図を統合し、ステーションなどの中ならこのような地図で表示し、
//     拡大すると星系図などシームレスに移行できたらいい」・決めた案 1・「A 切り替えボタンや検索も用意する」・「別ウインドウは残し、詳細の欄の地図はマップに切り替える方式にする」):
//     星系図に地図のある場所の枠 (MapMarks) を出し、拡大の上限でさらに拡大する・印を押す・検索で選ぶ・［詳細］の［地図表示］で、その場所の階層マップ (StationMapPanel) に
//     クロスフェードで切り替える。階層マップで縮小して小さくなる・［星系図］を押すと、その場所を真ん中にした星系図へ戻る。
//     現在位置が取り込み済みの地図のある場所の中なら、タブを初めて開いたときに階層マップから出す
public partial class MapPanel : UserControl
{
    // 並べる星系 (この順)
    internal static readonly string[] SystemNames = { "Stanton", "Pyro", "Nyx" };
    // 3 星系の並びの表示の名前 (App.Config.MapView)
    internal const string ViewAll = "3";

    // 設定を保存する (MainWindow.SaveConfigToFile を渡してもらう)
    public Action? SaveConfig { get; set; }
    // ［ゲームログ］の追い読みの今の着陸場所の名前 (GameLogPanel.CurrentLandingPlace を渡してもらう。分からなければ null)
    public Func<string?>? LandingPlaceProvider { get; set; }
    // ［ゲームログ］の追い読みの量子ジャンプの状態 (GameLogPanel.CurrentQuantumState を渡してもらう。追い読みが動いていない・Game.log が無ければ null)
    public Func<GameLogQuantumState?>? QuantumStateProvider { get; set; }
    // 「QT の前後で自動で現在位置を取る」の初めの状態 (Config)。InitializeComponent の後に入れる
    private void InitAutoCaptureCheck()
    {
        chkAutoCapture.IsChecked = App.Config.MapAutoCapture;
        chkAutoCapture.Checked += ChkAutoCapture_Changed;
        chkAutoCapture.Unchecked += ChkAutoCapture_Changed;
    }

    // ［ゲームログ］の追い読みを始める (GameLogPanel.EnsureStarted を渡してもらう。二重には始めない)。マップタブが見えたときに呼ぶ
    public Action? EnsureGameLogStarted { get; set; }

    // 今のページの表示 (ViewAll か星系名。ページを作っていなければ null)。書き換え (PostUpdate) は、このページに対して行う
    private string? _pageView;

    // 最後に描いた現在位置 (量子ジャンプ中の出発地が引けないときに使う) と、その鍵 (変わったときだけ描き直す)
    private CurrentLocation.EffectivePosition? _effective;
    private string _effectiveKey = "";
    // マップタブが見えている間、Game.log からの推定位置が変わったかを見る (2 秒ごと。変わらなければ描き直さない)
    private readonly System.Windows.Threading.DispatcherTimer _estimateTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    private bool _loadedOnce, _stale, _webReady, _webFailed;
    private Task? _webInit;
    private Task<ShopTree?>? _treeTask;
    private ShopTree? _tree;
    private int _renderVersion;

    public MapPanel()
    {
        InitializeComponent();
        InitAutoCaptureCheck();
        s_instance = this;
        InitPlaceMap();
        // 推定位置・量子ジャンプの状態・自転の向きが変わったら、ページを作り直さずに書き換える (PostUpdate。拡大率と表示の中心はそのまま)。
        // ページがまだ無い・表示が違うときだけ作る
        _estimateTimer.Tick += async (_, _) =>
        {
            if (!IsVisible || _tree == null) return;
            var now = Now();
            var eff = ComputeEffective(_tree);
            if (EffectiveKey(eff, now) == _effectiveKey) return;
            if (_pageView != null && _pageView == CurrentView() && _webReady) PostUpdate(_tree, eff, now);
            else await RenderAsync();
        };
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) { EnsureGameLogStarted?.Invoke(); _estimateTimer.Start(); }
            else _estimateTimer.Stop();
        };
        Loaded += async (_, _) =>
        {
            if (!_loadedOnce) { _loadedOnce = true; _stale = false; await RenderAsync(); }
            else if (_stale) { _stale = false; await RenderAsync(); }
        };
        // 星図の場所・店・紐づけの表が作り直されたら、ShopTree を捨てる。見えていればすぐ、見えていなければ次に開いたときに作り直す
        GameDataExtractor.ShopTablesChanged += () => Dispatcher.BeginInvoke(async () =>
        {
            _treeTask = null;
            if (!_loadedOnce) return;
            if (IsVisible) await RenderAsync(); else _stale = true;
        });
    }

    // 今の表示 ("3" か、SystemNames の 1 つ)。設定の値が分からなければ 3 星系
    private static string CurrentView()
    {
        var v = App.Config.MapView ?? "";
        return SystemNames.FirstOrDefault(n => n.Equals(v, StringComparison.OrdinalIgnoreCase)) ?? ViewAll;
    }

    private void BtnView_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string tag) return;
        App.Config.MapView = tag;
        SaveConfig?.Invoke();
        _ = RenderAsync();
    }

    // ===== WebView2 =====

    // 作業ディレクトリの webview2_map を WebView2 の利用者データの置き場にする (詳細タブの webview2_detail とは分ける)
    private async Task InitWebViewAsync()
    {
        if (_webReady || _webFailed) return;
        try
        {
            var folder = Path.Combine(DetailPanel.WorkDir(), "webview2_map");
            Directory.CreateDirectory(folder);
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: folder);
            await webMap.EnsureCoreWebView2Async(env);
            var core = webMap.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = true;
            // 中身は自分で作った HTML だけ。図の中のリンク (scjt://detail/…) は移動を止めて［詳細］タブでその場所を開く。ほかへ移る・新しい窓を開くことはさせない
            core.NavigationStarting += (_, e) =>
            {
                if (DetailHtml.TryParseLink(e.Uri, out var record))
                {
                    e.Cancel = true;
                    Dispatcher.BeginInvoke(() => OnMarkerClicked(record));
                    return;
                }
                if (e.IsUserInitiated) e.Cancel = true;
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.WebMessageReceived += (_, e) => OnMapMessage(e.WebMessageAsJson);
            core.NavigationCompleted += (_, _) => _navDone?.TrySetResult();
            webMap.DefaultBackgroundColor = System.Drawing.Color.White;
            _webReady = true;
            // 場所の施設のアイコン (ゲームの svg) を裏で取り出して割り当てる。割り当てられたら星系図を作り直す (それまではアイコン無し。AmenityIcons)
            _ = MapAmenityIconsAsync(core);
        }
        catch (Exception ex)
        {
            // WebView2 が使えない (ランタイムが無い等) ときは、図の代わりに断り書きを出す
            _webFailed = true;
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[マップ] WebView2 の初期化に失敗しました: {ex.GetType().FullName} (HResult 0x{ex.HResult:X8}): {ex}");
        }
    }

    // 施設のアイコンを割り当てられたか (星系図の場所のラベルの横に施設のアイコンを出す: GameDataQueryService.AmenityRowSvg)
    private bool _amenityIcons;
    private async Task MapAmenityIconsAsync(CoreWebView2 core)
    {
        if (!await AmenityIcons.MapAsync(core)) return;
        _amenityIcons = true;
        if (_pageView != null && IsVisible) await RenderAsync(); else if (_pageView != null) _stale = true;
    }

    // 図の点・ラベルの record を［詳細］タブで開く (名前は ShopTree の場所の名前。星系は星系名)
    private void OpenDetail(string record)
    {
        var name = _tree != null && _tree.NodesByRecord.TryGetValue(record, out var node) ? (node.IsStar ? node.System : node.Name) : "";
        DetailPanel.Request(record, name);
    }

    // 図のスクリプトからの「今の表示」({ map: 星系, cx, cy, w })。星系が SystemNames のどれかで、中心が有限の数、幅が正の有限の数のときだけ覚えて保存する
    private void OnMapMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            // 星系図のスクリプト (MapScript) のエラー: アプリのログに書く (同じ文は数秒に 1 回。StationMapWeb.LogScriptError)
            if (root.TryGetProperty("type", out var et) && et.ValueKind == JsonValueKind.String && et.GetString() == "scripterror")
            {
                string Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                LogScriptError("[マップ] 星系図のエラー", App.Config.MapView ?? "", Str("where"), Str("message"), Str("stack"));
                return;
            }
            // 拡大の上限でさらに拡大したときの、近くの地図のある場所 (MapScript の enterNear)
            if (root.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String && ty.GetString() == "enterplace")
            {
                if (root.TryGetProperty("rec", out var rc) && rc.GetString() is { Length: > 0 } rec && MapPlaceByRecordName(rec) is { } mp)
                    OpenPlace(mp.Record, mp.Name, null);
                return;
            }
            if (!root.TryGetProperty("map", out var m) || m.ValueKind != JsonValueKind.String) return;
            var system = SystemNames.FirstOrDefault(n => n.Equals(m.GetString(), StringComparison.OrdinalIgnoreCase));
            if (system == null) return;
            static double? Num(JsonElement r, string name) =>
                r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;
            if (Num(root, "cx") is not { } cx || Num(root, "cy") is not { } cy || Num(root, "w") is not { } w || w <= 0) return;
            var list = App.Config.MapViews;
            list.RemoveAll(v => string.Equals(v.System, system, StringComparison.OrdinalIgnoreCase));
            list.Add(new MapViewState { System = system, Cx = cx, Cy = cy, W = w });
            SaveConfig?.Invoke();
        }
        catch (JsonException) { }
    }

    // ===== 図 =====

    private async Task<ShopTree?> GetTreeAsync()
    {
        var task = _treeTask ??= Task.Run(() =>
        {
            var db = DetailPanel.DbPath();
            if (!File.Exists(db)) return (ShopTree?)null;
            using var q = GameDataQueryService.OpenReadOnly(db);
            return q.BuildShopTree(DetailPanel.TradeDbPath());
        });
        try { return await task; }
        catch
        {
            if (ReferenceEquals(_treeTask, task)) _treeTask = null;
            throw;
        }
    }

    // 今の表示で図を出し直す (ShopTree は覚えたものを使う。無ければ作る)。終わったら上の知らせを statusAfter にする (null なら空)
    private async Task RenderAsync(string? statusAfter = null)
    {
        var version = ++_renderVersion;
        txtStatus.Text = "読み込み中...";
        try
        {
            await (_webInit ??= InitWebViewAsync());
            var tree = await GetTreeAsync();
            if (version != _renderVersion) return;
            _tree = tree;
            RefreshAmenityFilter(tree);
            RefreshMapHints();
            txtStatus.Text = statusAfter ?? "";
            if (tree == null || tree.IsEmpty) { ShowMessage("インデックスが未構築です (設定タブの［インデックス構築］)"); return; }
            if (_webFailed || !_webReady) { ShowMessage("（図は WebView2 が必要です）"); return; }
            var eff = ComputeEffective(tree);
            _effective = eff;
            _effectiveKey = EffectiveKey(eff, Now());
            var view = CurrentView();
            _marks = BuildMarks(tree, eff?.Unix ?? Now());
            var (html, count) = BuildPage(tree, view, App.Config.MapViews, eff, LogPlaceNode(tree, LandingPlaceProvider?.Invoke()), LogRunning(), ComputeQt(tree),
                QuantumStateProvider?.Invoke(), _marks, _amenityIcons ? AmenityIcons.Url : null);
            if (count == 0) { ShowMessage("座標のある星系がありません"); _pageView = null; return; }
            txtMessage.Visibility = Visibility.Collapsed;
            // 3D を出している間は、星系図は隠したまま (書き換えだけする)
            if (pnlPlace.Visibility != Visibility.Visible) webMap.Visibility = Visibility.Visible;
            _navDone = new TaskCompletionSource();
            webMap.CoreWebView2.NavigateToString(html);
            _pageView = view;
            AfterRender(tree);
        }
        catch (Exception ex)
        {
            if (version != _renderVersion) return;
            txtStatus.Text = "";
            ShowMessage($"マップを作れませんでした: {ex.Message}");
        }
    }

    private void ShowMessage(string text)
    {
        txtMessage.Text = text;
        txtMessage.Visibility = Visibility.Visible;
        webMap.Visibility = Visibility.Collapsed;
    }

    // ===== 現在位置 =====

    // /showlocation の前にクリップボードに入れておく文字 (前の座標を読み違えないように。親が確かめた PowerShell と同じやり方)
    private const string ClipboardPlaceholder = "(showlocation を待っています)";

    // ［現在位置］: ゲームに /showlocation を送って座標を読み、現在位置の星系 1 つの表示にする (CaptureAsync。自動の取得と同時には動かない)。
    // 送る前に、今わかっている現在位置の星系 (KnownCurrentSystem) が今の表示 (3 星系の並びも含む) と違えば、先にその星系 1 つの表示にする
    // (星系のボタンと同じ切り替え方。中心・幅は MapViews に覚えた表示)。切り替えている間も取得中と同じ扱いにする
    // (ボタンを押せなくし、自動の取得を割り込ませない)。取得中 (自動の取得の途中) に押されたときは切り替えない
    private async void BtnCurrent_Click(object sender, RoutedEventArgs e) => await CaptureCurrentAsync();

    // ［現在位置］と同じ処理の公開の入口 (ステーションの 3D (新しい方式) の［現在位置］(StationView3DWindow.CaptureHook) からも呼ぶ)。
    // 戻り値: 座標を取れたか (取得中に呼ばれたときは false) と、そのときのマップタブの状態の欄の文 (取れなかった理由など)。
    // マップタブが開かれていない (この欄が画面に無い) ときは、図の作り直しを次に開いたときに回す (RenderOrDeferAsync。
    // 画面に無い WebView2 の初期化は画面に出るまで終わらないので、そのまま待つと取得中のまま止まり、自動の取得も動かなくなる)
    // from: 押したボタンの画面 (アプリのログの「（ボタン・<from>）」に出す。既定は［マップ］タブの［現在位置］)
    public async Task<(bool Ok, string Status)> CaptureCurrentAsync(string from = "［マップ］タブ")
    {
        if (_capturing) return (false, "現在位置を取っている途中です");
        if (KnownCurrentSystem() is { } known && !known.Equals(CurrentView(), StringComparison.OrdinalIgnoreCase))
        {
            _capturing = true;
            btnCurrent.IsEnabled = false;
            try
            {
                App.Config.MapView = known;
                SaveConfig?.Invoke();
                await RenderOrDeferAsync();
            }
            finally
            {
                _capturing = false;
                btnCurrent.IsEnabled = true;
            }
        }
        var ok = await CaptureAsync(auto: false, from);
        // (2026-11-02、利用者の指摘「現在位置ボタンで位置がとれないとでる」) 座標を読めなかったときは、1 回だけ取り直す。
        // 間は自動の取得の取り直し (目的地の選択の後、取れなければ 3 秒おきにあと 2 回) と同じ 3 秒
        if (!ok && _lastFailUnreadable)
        {
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[マップ] 座標を読めなかったので、{ButtonRetryDelay.TotalSeconds:0} 秒後に 1 回だけ取り直します（ボタン・{from}）");
            await Task.Delay(ButtonRetryDelay);
            ok = await CaptureAsync(auto: false, from + "・取り直し");
        }
        // 取れなかったときの Game.log の手がかり (ステーションの地図がハンガーにいるかの推測に使う。StationMapPanel)
        // 時刻は UTC (Game.log の行の時刻 (GameLogParser) と同じ)
        LastCaptureFailLog = ok ? null : (DateTime.UtcNow, LandingPlaceProvider?.Invoke(), QuantumStateProvider?.Invoke());
        return (ok, txtStatus.Text);
    }

    // ボタンで座標を読めなかったときの取り直しまでの間 (CaptureCurrentAsync)
    private static readonly TimeSpan ButtonRetryDelay = TimeSpan.FromSeconds(3);
    // 直前の取得の失敗が「座標を読めなかった」(クリップボードの文字が座標の形でない) か
    private bool _lastFailUnreadable;
    // ボタンで取れなかったときの Game.log の手がかり (取れなかった時刻 (UTC)・今の着陸場所の名前・量子ジャンプの状態)。取れたら null。アプリを動かしている間だけ
    public static (DateTime At, string? Landing, GameLogQuantumState? Quantum)? LastCaptureFailLog { get; private set; }

    // ログに出すクリップボードの文字の頭 (ClipHeadLength 文字。改行は「⏎」にして見えるように。空・読めなければその旨)
    private const int ClipHeadLength = 80;
    private static string ClipHead(string? text)
    {
        if (text == null) return "（読めなかった）";
        if (text.Length == 0) return "（空）";
        if (text == ClipboardPlaceholder) return "（送る前に入れた印のまま。ゲームが座標を書かなかった）";
        var t = text.Length > ClipHeadLength ? text[..ClipHeadLength] + "…" : text;
        return "「" + t.Replace("\r\n", "⏎").Replace("\n", "⏎").Replace("\r", "⏎").Replace("\t", "→") + "」";
    }

    // 図を作り直す。この欄が画面に無いときは作り直さず、次に画面に出たとき (Loaded) に作る (知らせの文は状態の欄に出す)
    private async Task RenderOrDeferAsync(string? statusAfter = null)
    {
        if (IsLoaded) { await RenderAsync(statusAfter); return; }
        _stale = true;
        txtStatus.Text = statusAfter ?? "";
    }

    // 今わかっている現在位置の星系 (SystemNames の 1 つ)。図と同じ求め方 (ComputeEffective。星系は MapState と同じく eff.System があればその星系、
    // 無ければ CurrentLocation.Locate)。現在位置が無い・ShopTree が無い (インデックス未構築等)・星系が分からなければ null
    private string? KnownCurrentSystem()
    {
        if (_tree is not { IsEmpty: false } tree) return null;
        if (ComputeEffective(tree) is not { NoPos: false } eff) return null;
        var effSystem = eff.System is { Length: > 0 } es ? tree.Systems.FirstOrDefault(s => s.System.Equals(es, StringComparison.OrdinalIgnoreCase)) : null;
        var loc = CurrentLocation.Locate(tree, eff.Pos, eff.Unix, SystemNames, null, fresh: false, effSystem);
        return loc == null ? null : SystemNames.FirstOrDefault(n => n.Equals(loc.System.System, StringComparison.OrdinalIgnoreCase));
    }

    // 座標を取っている途中か (［現在位置］と自動の取得を同時に動かさない)、最後に自動で取った時刻 (UNIX 秒)
    private bool _capturing;
    // ［現在位置］のボタン (どの画面のものでも) で最後に取った座標の時刻 (App.Config.MapCurrent.Utc と同じ値。まだ無ければ 0。アプリを動かしている間だけ)
    public static double LastButtonCaptureUtc { get; private set; }
    private double _lastAutoCapture;

    // ［ゲームログ］の追い読みで量子ジャンプの目的地の選択 (kind = "select") か到着 (kind = "arrive") の行を見た (GameLogPanel.QuantumEventSeen)。
    // 「QT・着陸・ゲーム開始のときに自動で現在位置を取る」(旧「QT の前後で自動で現在位置を取る」) がオンなら座標を取る (CaptureAsync(auto: true))。マップタブが見えていなくても動く。
    //   目的地の選択: 行を見たらすぐ (跳ぶ前に出発地を取るため)。取れなかった (座標を読めない・ゲームの窓が前に無い) ら、3 秒おきにあと 2 回まで試す。
    //     量子ジャンプが移動中になったら、その時点でやめる。ボタンの取得の途中なら、その回は飛ばす
    //     (星図 (mobiGlas など) を開いている間はチャットに届かず取れないことがあるため)
    //   到着: 2 秒後に 1 回。2 区間の経路の区間 1 の終わり (kind = "leg1")・着陸場に着いた (kind = "land"。量子ジャンプの後でなくても。
    //   到着の行が出ない最後の区間の着陸もこれ)・ゲームの世界に出た (kind = "enter"。読み込み画面が閉じた・復活) も同じく 2 秒後に 1 回 (自転の補正の位置合わせのため)。
    //   前に自動で取り始めてから 10 秒以内なら始めない (着陸と到着が続いたときなどは最初の 1 回だけ) (同じ目的地の選び直しが続いたときは最初の 1 回だけ。再試行には当てない)
    //   移動中になった (kind = "move"): 到着予定の速さを測る座標を取る (OnQuantumMoving)。上の 10 秒の決まりは当てず、自動で取り始めた時刻も変えない
    //     (到着の自動の取得を妨げないため)
    //   目的地を選んだ後に離陸してステーション・着陸場の区域を出た (kind = "exit"。GameLogQuantumState.TakeoffExitAt): 移動中になったときと同じ決まりで座標を取る (OnQuantumTakeoffExit)
    public void OnQuantumEvent(string kind)
    {
        if (!App.Config.MapAutoCapture) return;
        if (kind == "move") { OnQuantumMoving(); return; }
        if (kind == "exit") { OnQuantumTakeoffExit(); return; }
        if (Now() - _lastAutoCapture < AutoCaptureMinIntervalSeconds) return;
        _lastAutoCapture = Now();
        var select = kind == "select";
        _ = Dispatcher.InvokeAsync(async () =>
        {
            if (!select) await Task.Delay(TimeSpan.FromSeconds(2));   // 目的地の選択はすぐ取りに行く。到着・区間 1 の終わり・着陸・ゲームの世界に出たときは 2 秒後
            var tries = select ? 3 : 1;
            for (int i = 0; i < tries; i++)
            {
                if (i > 0) await Task.Delay(TimeSpan.FromSeconds(3));
                if (select && QuantumStateProvider?.Invoke() is { Moving: true }) break;   // 跳んだ後は出発地にならないのでやめる
                if (_capturing) continue;   // ボタンの取得の途中: この回は飛ばす
                if (await CaptureAsync(auto: true)) break;
            }
        });
    }

    // 量子ジャンプが移動中になった: 移動中になった時刻 (MovingSince) の 15 秒後と 45 秒後に座標を取る (今がもう過ぎていればすぐ)。
    // 各回、取れなかったら 3 秒おきにあと 2 回まで試す。試す前に、移動中でなくなった・目的地を選び直した (TargetTime が変わった) ならやめる (その後の回もやめる)。
    // ボタンの取得の途中なら、その試しは飛ばす
    private void OnQuantumMoving()
    {
        if (QuantumStateProvider?.Invoke() is not { Moving: true, MovingSince: { } since } q) return;
        var targetTime = q.TargetTime;
        var sinceUnix = (since - DateTime.UnixEpoch).TotalSeconds;   // MovingSince は UTC
        _ = Dispatcher.InvokeAsync(async () =>
        {
            foreach (var after in MoveCaptureDelaysSeconds)
            {
                var wait = sinceUnix + after - Now();
                if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait));
                for (int i = 0; i < 3; i++)
                {
                    if (i > 0) await Task.Delay(TimeSpan.FromSeconds(3));
                    if (QuantumStateProvider?.Invoke() is not { Moving: true } cq || cq.TargetTime != targetTime) return;   // 着いた・選び直した: この後の回もやめる
                    if (_capturing) continue;   // ボタンの取得の途中: この試しは飛ばす
                    if (await CaptureAsync(auto: true)) break;
                }
            }
        });
    }

    // 目的地を選んだ後に離陸し、ステーション・着陸場の区域を出た (TakeoffExitAt): 区域を出た時刻の 15 秒後と 45 秒後に座標を取る (今がもう過ぎていればすぐ)。
    // 離陸の後の最初の Location の変化は移動中にしないので、そのまま跳ぶと移動中にならない (2026-10-31 15:14 の出発)。決まりは OnQuantumMoving と同じ:
    // 各回、取れなかったら 3 秒おきにあと 2 回まで試す。試す前に、到着した (QtActive でない)・目的地を選び直した (TargetTime が変わった) ならやめる (その後の回もやめる)。
    // ボタンの取得の途中なら、その試しは飛ばす
    private void OnQuantumTakeoffExit()
    {
        if (QuantumStateProvider?.Invoke() is not { TakeoffExitAt: { } exitAt } q || !QtActive(q)) return;
        var targetTime = q.TargetTime;
        var sinceUnix = (exitAt - DateTime.UnixEpoch).TotalSeconds;   // TakeoffExitAt は UTC
        _ = Dispatcher.InvokeAsync(async () =>
        {
            foreach (var after in MoveCaptureDelaysSeconds)
            {
                var wait = sinceUnix + after - Now();
                if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait));
                for (int i = 0; i < 3; i++)
                {
                    if (i > 0) await Task.Delay(TimeSpan.FromSeconds(3));
                    if (QuantumStateProvider?.Invoke() is not { } cq || !QtActive(cq) || cq.TargetTime != targetTime) return;   // 着いた・選び直した: この後の回もやめる
                    if (_capturing) continue;   // ボタンの取得の途中: この試しは飛ばす
                    if (await CaptureAsync(auto: true)) break;
                }
            }
        });
    }

    // 移動中になってから座標を取るまでの秒数 (この順)
    private static readonly double[] MoveCaptureDelaysSeconds = { 15, 45 };

    // 自動の取得の間隔の下限 (秒)
    private const double AutoCaptureMinIntervalSeconds = 10;

    private void ChkAutoCapture_Changed(object sender, RoutedEventArgs e)
    {
        App.Config.MapAutoCapture = chkAutoCapture.IsChecked == true;
        SaveConfig?.Invoke();
    }

    // ゲームに /showlocation を送って座標を読む。
    // クリップボードの中身は送る前に全部の形式を覚えて、読んだ後に戻す (元が空だったときは空にする。クリップボードを読めない・1 つでも形式を覚えられなければ送らない。
    // 戻せなければ状態の欄とアプリのログで知らせる)。
    // ［現在位置］(auto = false): ゲームの窓を前に出して送り、読み終えたらアプリの窓を前に戻す。現在位置の星系 1 つの表示にし、現在位置を中心にしてページを作り直す。
    // 自動 (auto = true): ゲームの窓が前に無ければ送らない。読み終えてもアプリの窓を前に出さない。表示・中心は変えず、ページがあれば書き換えだけ。
    // どちらも、取った座標・補正値・Location の番号の覚えは同じ。取っている間はもう片方を動かさない (ボタンは押せない・自動はやめる)。
    // 量子ジャンプの途中 (ComputeQt が null でない) に取ったら、星系は線の星系 (QtState.System、無ければ目的地の星系) で決め、覚える居場所もその星系で決める
    // (取った座標に星系の名前 (MapCurrentState.System) を入れる)
    // 座標を取れたら true
    // from: ボタンのとき、押したボタンの画面 (［マップ］タブ・ステーションの地図など。アプリのログに出す)
    private async Task<bool> CaptureAsync(bool auto, string from = "［マップ］タブ")
    {
        if (_capturing) return false;
        _capturing = true;
        // 取れなかったとき: 画面の知らせ (status) を出し、アプリのログに「[マップ] 現在位置を取れませんでした（自動 / ボタン・<画面>）: <理由>」を出す (理由は無ければ status)
        var mode = auto ? "自動" : $"ボタン・{from}";
        // クリップボードを元に戻せなかったときの知らせ (状態の欄の終わりに添える。戻せたときは空)
        var clipboardNote = "";
        _lastFailUnreadable = false;
        bool Fail(string status, string? reason = null)
        {
            txtStatus.Text = status + clipboardNote;
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[マップ] 現在位置を取れませんでした（{mode}）: {reason ?? status}");
            return false;
        }
        btnCurrent.IsEnabled = false;
        try
        {
            var game = ShowLocationSender.FindGameWindow();
            if (game == IntPtr.Zero) return Fail("ゲーム (Star Citizen) が起動していません");
            if (auto && !ShowLocationSender.IsForeground(game)) return Fail("ゲームの窓が前に無かったので自動の取得をしませんでした");
            if (!auto)
            {
                // ボタン: ゲームの窓を前に出す (Alt を押して離してから。600ms 待つ)。前に出なければ送らない
                ShowLocationSender.BringToFront(game);
                await Task.Delay(600);
                if (!ShowLocationSender.IsForeground(game))
                {
                    BringAppToFront();
                    return Fail("ゲームの窓を前に出せませんでした (キーは送っていません)");
                }
            }
            // 送る前に mobiGlas が開いているかを画面で確かめる (MobiGlasDetector。ゲームの窓の下から 12% を撮って OCR)。開いていれば送らない。
            // 確かめられない (撮れない・OCR が使えない) ときは、確かめずに送る (知らせを添える)。判定の時間はアプリのログに出す
            var ocrNote = "";
            try
            {
                var mg = await MobiGlasDetector.CheckWindowAsync(game);
                App.AppendStartupLog(App.Config.WorkingDirectory,
                    $"[マップ] mobiGlas の確認: {(mg.Open ? "開いている" : "閉じている")} ({mg.Elapsed.TotalMilliseconds:0} ms。読めた言葉: {string.Join("・", mg.Hits)})");
                if (mg.Open)
                {
                    if (!auto) BringAppToFront();
                    return Fail(auto ? "mobiGlas が開いているので自動の取得をしませんでした" : "mobiGlas が開いているので送りませんでした（閉じてからもう一度押してください）");
                }
            }
            catch (Exception ex)
            {
                ocrNote = $" (mobiGlas が開いているかを確かめられなかったので、確かめずに送りました: {ex.Message})";
                App.AppendStartupLog(App.Config.WorkingDirectory, $"[マップ] mobiGlas の確認ができませんでした: {ex.GetType().FullName}: {ex.Message}");
            }
            // 今のクリップボードの中身を、形式ごとに新しい DataObject に写して覚える。
            // クリップボードを読むこと自体ができない (ほかのアプリが使っている) ときは、ゲームに送らずにやめる。
            // 元に形式が 1 つ以上あるのに 1 つでも覚えられなかった (null・例外) ときも、ゲームに送らずにやめる (クリップボードには触らない)
            DataObject saved;
            int originalFormats = 0;
            int savedFormats = 0;
            try
            {
                var current = Clipboard.GetDataObject();
                saved = new DataObject();
                var formats = current?.GetFormats(false) ?? Array.Empty<string>();
                originalFormats = formats.Length;
                foreach (var f in formats)
                {
                    try
                    {
                        if (current!.GetData(f) is { } data) { saved.SetData(f, data); savedFormats++; }
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception)
            {
                return Fail("クリップボードを使えなかったので送りませんでした");
            }
            if (originalFormats > 0 && savedFormats < originalFormats)
            {
                return Fail("クリップボードの中身を覚えられなかったので送りませんでした（クリップボードの中身はそのままです）",
                    $"クリップボードの中身を覚えられなかったので送りませんでした（形式 {originalFormats} のうち覚えられたのは {savedFormats}）");
            }
            txtStatus.Text = (auto ? "自動で /showlocation を送っています..." : "ゲームに /showlocation を送っています...") + ocrNote;
            try { Clipboard.SetText(ClipboardPlaceholder); } catch (Exception) { }
            ShowLocationSender.SendResult sent;
            string? text = null;
            // 最後の Enter を押した時刻 (ゲームが座標を出した時刻。取った座標の時刻にする)
            var enterUtc = new System.Runtime.CompilerServices.StrongBox<double>(double.NaN);
            try
            {
                // ゲームの窓はもう前にある (ボタンは上で前に出し、自動は前にあることを確かめた) ので、ここでは前に出さない
                sent = await Task.Run(() => ShowLocationSender.Send(game, bringToFront: false, enterUtc));
                if (sent == ShowLocationSender.SendResult.Sent)
                {
                    await Task.Delay(1500);   // 最後の Enter から 1500ms 後に読む
                    try { text = Clipboard.GetText(); } catch (Exception) { }
                }
            }
            finally
            {
                // 覚えた中身を戻す (元が空だった (形式 0) ときは空にする)。失敗したら 100ms おいて 3 回までやり直す。
                // それでも戻せなければ、状態の欄とアプリのログに「クリップボードを元に戻せませんでした」を出す
                Exception? restoreError = null;
                for (int attempt = 0; attempt <= 3; attempt++)
                {
                    if (attempt > 0) await Task.Delay(100);
                    try
                    {
                        if (savedFormats > 0) Clipboard.SetDataObject(saved, true); else Clipboard.Clear();
                        restoreError = null;
                        break;
                    }
                    catch (Exception ex) { restoreError = ex; }
                }
                if (restoreError != null)
                {
                    clipboardNote = " (クリップボードを元に戻せませんでした)";
                    App.AppendStartupLog(App.Config.WorkingDirectory, $"[マップ] クリップボードを元に戻せませんでした（{mode}）: {restoreError.GetType().FullName}: {restoreError.Message}");
                }
                if (!auto) BringAppToFront();
            }
            if (sent == ShowLocationSender.SendResult.NotFront)
            {
                return Fail(auto ? "ゲームの窓が前に無かったので自動の取得をしませんでした" : "ゲームの窓を前に出せませんでした (キーは送っていません)");
            }
            if (sent == ShowLocationSender.SendResult.Interrupted)
            {
                return Fail("送っている途中でゲームの窓が前から外れたので止めました。ゲームのチャット欄に打ちかけの文字が残っていれば消してください");
            }
            if (CurrentLocation.Parse(text) is not { } pos)
            {
                // (2026-11-02) 読めなかったクリップボードの文字の頭をアプリのログに出す (理由を後で調べられるように)
                _lastFailUnreadable = true;
                return Fail("座標を読めませんでした (クリップボードの文字が /showlocation の座標の形ではありません)",
                    $"座標を読めませんでした (クリップボードの文字が /showlocation の座標の形ではありません。文字の頭: {ClipHead(text)})");
            }
            var tree = await GetTreeAsync();
            if (tree == null || tree.IsEmpty) { ShowMessage("インデックスが未構築です (設定タブの［インデックス構築］)"); return Fail("", "インデックスが未構築です"); }
            // 取った座標の時刻: ゲームが座標を出した、最後の Enter を押した時刻 (ミリ秒まで)。前は 1.5 秒待ってクリップボードを読み、場所の木を取った後の時刻で、
            // 1.5 秒以上遅れていた (自転・公転を当てる時刻がずれる)。Enter の時刻が無いとき (送った結果が Sent でない場合はここに来ない) だけ今の時刻
            var now = double.IsFinite(enterUtc.Value) ? enterUtc.Value : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            // 量子ジャンプの目的地を選んでから到着するまで (QtActive) なら、到着予定を出す点 (座標, 取った時刻) に足す (目的地を選んだ時刻 TargetTime ごと。違えば前の点を捨てる)。
            // 移動中かどうかは問わない (量子ジャンプを始めた行は Game.log に出ない。目的地を選んだときの点は出発の位置に使う)
            var qNow = QuantumStateProvider?.Invoke();
            var pointAdded = false;
            if (qNow != null && QtActive(qNow))
            {
                if (qNow.TargetTime != _qtPointsTargetTime) { _qtPoints.Clear(); _qtPointsTargetTime = qNow.TargetTime; }
                _qtPoints.Add((pos, now));
                pointAdded = true;
            }
            // 量子ジャンプの途中なら、星系は線の星系にする (App.Config.MapCurrent を入れ替える前に求める)
            var qt = ComputeQt(tree);
            // 点を足して到着予定を出せれば、アプリのログに出す (根拠: ドライブの名前か「実測から」を添える)
            if (pointAdded && qNow != null && qt != null && QtEta(qNow, qt) is { Eta: { } etaUnix } eta)
                App.AppendStartupLog(App.Config.WorkingDirectory, string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"[マップ] QT の到着予定: {LocalTime(etaUnix)}（速さ {eta.Speed / 1e6:0.0} Mm/s、残り {GameDataQueryService.FormatDistance(eta.Remain)}、{eta.Basis}）"));
            var qtSystem = qt?.System ?? qt?.Target?.Root;
            var res = CurrentLocation.Locate(tree, pos, now, SystemNames, LogPlaceNode(tree, LandingPlaceProvider?.Invoke()), fresh: true, qtSystem);
            if (res == null) return Fail("現在位置の星系が分かりませんでした");
            // 取れた: アプリのログに座標・星系・いちばん近い場所を出す
            App.AppendStartupLog(App.Config.WorkingDirectory, string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"[マップ] 現在位置を取りました（{mode}）: 時刻 {DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(now * 1000)).ToLocalTime():HH:mm:ss.fff}（最後の Enter）、x{pos.X:0.0} y{pos.Y:0.0} z{pos.Z:0.0}（星系 {res.System.System}、いちばん近い場所 {(res.Near.FirstOrDefault() is { } nr ? $"{nr.Node.Name} {GameDataQueryService.FormatDistance(nr.Distance)}" : "なし")}）"));
            var system = SystemNames.First(n => n.Equals(res.System.System, StringComparison.OrdinalIgnoreCase));
            // 取ったときの Game.log の Location の番号と、そのときの「どこにいたか」を組にして覚える (番号ごとに最新の 1 つ。CurrentLocation.Classify)。
            // 覚えたリストは書き換えず、新しいリストに入れ替える
            var locationId = QuantumStateProvider?.Invoke()?.LocationId ?? "";
            App.Config.MapCurrent = new MapCurrentState { X = pos.X, Y = pos.Y, Z = pos.Z, Utc = now, LocationId = locationId, System = qtSystem?.System ?? "" };
            // ボタンで取った座標の時刻 (ステーションの地図は、これが今の現在位置の時刻と同じなら、どの画面のボタンで取ったときも出発地を現在位置にする)
            if (!auto) LastButtonCaptureUtc = now;
            if (locationId.Length > 0 && CurrentLocation.Classify(tree, pos, now, SystemNames, LogPlaceNode(tree, LandingPlaceProvider?.Invoke()), qtSystem) is { } where)
            {
                var memories = App.Config.MapLocationMemories.Where(m => m.LocationId != locationId).ToList();
                memories.Add(new MapLocationMemory { LocationId = locationId, Record = where.Record, Kind = where.Kind, Utc = now });
                App.Config.MapLocationMemories = memories;
            }
            if (auto)
            {
                // 自動: 表示と中心は変えない。ページがあれば書き換えだけ (無ければ、マップタブを開いたときに出る)
                SaveConfig?.Invoke();
                txtStatus.Text = $"自動で現在位置を取得しました ({LocalTime(now)})" + ocrNote + clipboardNote;
                if (_pageView != null && _pageView == CurrentView() && _webReady && _tree != null) PostUpdate(_tree, ComputeEffective(_tree), Now());
                return true;
            }
            App.Config.MapView = system;
            // 最初の表示: 現在位置を中心に、いちばん近い場所までの距離 × 6 (20 Km 以上) が枠の幅に入る倍率 (操作後の表示と同じ覚え方で覚える)
            App.Config.MapViews.RemoveAll(v => string.Equals(v.System, system, StringComparison.OrdinalIgnoreCase));
            App.Config.MapViews.Add(new MapViewState { System = system, Cx = pos.X, Cy = pos.Y, W = Math.Max(res.NearestDistance * 6, 20000) });
            SaveConfig?.Invoke();
            await RenderOrDeferAsync($"現在位置を取得しました ({LocalTime(now)})" + ocrNote + clipboardNote);
            return true;
        }
        catch (Exception ex)
        {
            return Fail($"現在位置を取れませんでした: {ex.Message}", ex.Message);
        }
        finally
        {
            _capturing = false;
            btnCurrent.IsEnabled = true;
        }
    }

    // 使う現在位置 (取った座標・Game.log からの推定。CurrentLocation.Effective)。［ゲームログ］の追い読みが動いていなければ取った座標だけ
    private CurrentLocation.EffectivePosition? ComputeEffective(ShopTree tree)
    {
        var q = QuantumStateProvider?.Invoke();
        return CurrentLocation.Effective(tree, App.Config.MapCurrent, Now(), q != null, q != null ? LandingPlaceProvider?.Invoke() : null, q, _effective);
    }

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    // 量子ジャンプの状態 (CurrentLocation.Quantum。追い読みが動いていなければ null)。
    // 移動中・途中で停止の間は線を引き直さない: その QT (目的地を選んだ時刻 q.TargetTime で見分ける) で最初に From と To が両方ある結果が出たら、
    // その状態を丸ごと覚えて (From・To・Target・System・Goal・FromSource)、同じ QT の間は覚えたものを返す。
    // ただし行 (Line) は、最後の「: 」より後ろを今の状態の言葉 (CurrentLocation.QtStatus) に置き換える。From か To が null の結果は覚えない (その回の結果を返す)。
    // Quantum が null・目的地を選び直した (TargetTime が変わった)・設定に戻った (移動中でも停止でもない)・ShopTree が作り直された なら覚えを捨て、その回の結果を返す。
    // 覚えはアプリの中だけ (保存しない)。
    // 途中で停止 (q.Stopped) でも、到着予定 (QtEta) の時刻 + QtStopEtaMargin より前か、取った点が目的地へ進み続けている (StopOverridden) なら、
    // 停止にしない (移動中として扱う。目的地の区域とは別の区域に入った後も量子ジャンプが続くことがあるため)。
    // 返す前に、到着予定を出せれば (QtEta) 行の末尾に「（到着予定 18:06:00 頃・推定）」を付ける (覚えた状態には付けない)。2 区間の経路で区間 1 がまだ終わっていなければ
    // 「（到着予定 18:06:00 頃・推定、途中で 1 回止まります）」。進んだ点があるのに出せなければ「（到着予定を出せません: <理由>）」
    // 求める途中で例外が出たら、null を返し (マップの書き換え・タイマーは止めない)、アプリのログに出す (同じ内容が続く間は 1 回だけ)
    private CurrentLocation.QtState? ComputeQt(ShopTree tree)
    {
        try { return ComputeQtCore(tree); }
        catch (Exception ex)
        {
            var msg = $"[マップ] 量子ジャンプの状態を求められませんでした: {ex.GetType().FullName}: {ex.Message}";
            if (msg != _computeQtLastError)
            {
                _computeQtLastError = msg;
                App.AppendStartupLog(App.Config.WorkingDirectory, msg);
            }
            return null;
        }
    }
    // ComputeQt で最後にアプリのログに出した例外の内容 (同じ内容を 2 秒ごとに出し続けないため)
    private string? _computeQtLastError;

    private CurrentLocation.QtState? ComputeQtCore(ShopTree tree)
    {
        var q = QuantumStateProvider?.Invoke();
        var qt = q == null ? null : CurrentLocation.Quantum(tree, q, App.Config.MapCurrent, Now(), LandingPlaceProvider?.Invoke(), SystemNames);
        if (q is { Stopped: true } && qt != null && StopOverridden(q, qt))
        {
            q = q with { Stopped = false };
            qt = CurrentLocation.Quantum(tree, q, App.Config.MapCurrent, Now(), LandingPlaceProvider?.Invoke(), SystemNames);
        }
        if (qt == null || !(q!.Moving || q.Stopped) || q.TargetTime != _qtFixedTargetTime || !ReferenceEquals(tree, _qtFixedTree))
        { _qtFixedTargetTime = null; _qtFixed = null; _qtFixedTree = null; }
        if (qt == null) return null;
        CurrentLocation.QtState result;
        if (!(q!.Moving || q.Stopped)) result = qt;
        else if (_qtFixed is { } fixedQt)
        {
            var cut = fixedQt.Line.LastIndexOf(": ", StringComparison.Ordinal);
            result = cut < 0 ? fixedQt : fixedQt with { Line = fixedQt.Line[..(cut + 2)] + CurrentLocation.QtStatus(q) };
        }
        else
        {
            if (qt.From != null && qt.To != null)
            {
                _qtFixedTargetTime = q.TargetTime;
                _qtFixed = qt;
                _qtFixedTree = tree;
            }
            result = qt;
        }
        var legNote = q.RouteFinalIndex >= 1 && !(q.Leg1EndedAt is { } le && le >= q.TargetTime) ? "、途中で 1 回止まります" : "";
        return QtEta(q, result) switch
        {
            { Eta: { } eta } => result with { Line = result.Line + $"（到着予定 {LocalTime(eta)} 頃・推定{legNote}）" },
            { Reason: { } reason } => result with { Line = result.Line + $"（到着予定を出せません: {reason}）" },
            _ => result,
        };
    }

    // 途中で停止を取り消す余裕 (秒): 到着予定の時刻からこれだけ過ぎるまでは停止にしない。停止の判定の待ち (GameLogParser.QuantumStopWait) と同じ 15 秒。
    // 実測 (2026-10-21〜11-01 の点 35 個) では、実際の到着 (2 区間の経路は区間 1 の終わり) が到着予定より遅れたのは、出発の位置が分かる点で最大 2.2 秒、
    // 出発の位置が分からない点 (2026-10-28 の Orison) で 11.8 秒。2 区間の経路で区間 1 の終わりが分からなかった回 (2026-10-26 の Pyro のビーコン) は 66〜67 秒遅れた
    // (この回は停止になる。区間 1 の終わりで止まっているので「もう一度 QT が必要」は正しい)。
    // 区域に入った後も QT が続いた 29 回 (区域に入ってから到着まで 23〜267 秒) は、到着予定より前に到着していれば停止にならない
    private const double QtStopEtaMargin = 15;
    // 「取った点が目的地へ進み続けている」に使う点の新しさ (秒): 最新の点が今からこれより古ければ使わない
    private const double QtProgressFreshSeconds = 30;

    // 途中で停止を取り消すか: 正しい到着予定 (EstimateQtEta が筋の通らない結果を外した後の Eta。有限の値) があり、今が到着予定 + QtStopEtaMargin より前。
    // または、最後に Location が変わった (停止の候補になった) 後に取った続きの 2 点で、目的地までの距離が 1 Mm 以上縮んでいて、最新の点が QtProgressFreshSeconds 以内に取ったもの
    private bool StopOverridden(GameLogQuantumState q, CurrentLocation.QtState qt)
    {
        if (QtEta(q, qt) is { Eta: { } eta } && double.IsFinite(eta) && Now() <= eta + QtStopEtaMargin) return true;
        if ((qt.Dest ?? qt.To) is not { } dest || q.TargetTime != _qtPointsTargetTime || q.LastLocationChange is not { } changed) return false;
        var since = (changed - DateTime.UnixEpoch).TotalSeconds;
        var pts = _qtPoints.Where(p => p.Unix >= since).ToList();
        if (pts.Count < 2 || Now() - pts[^1].Unix > QtProgressFreshSeconds) return false;
        static double Dist((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
            Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
        return Dist(pts[^2].Pos, dest) - Dist(pts[^1].Pos, dest) >= 1e6;
    }

    // 量子ジャンプの目的地を選んでから到着するまでか (到着・目的地を選んだ後の着陸・ハンガー進入の前)
    private static bool QtActive(GameLogQuantumState q) =>
        q.TargetTime is { } tt && !(q.ArrivedTime is { } a && a >= tt) && !(q.LandingSince is { } ls && ls > tt) && !(q.LastHangarEnter is { } he && he > tt);

    // 到着予定 (CurrentLocation.EstimateQtEta)。今の QT (TargetTime) で取った点と、目的地の位置 (qt.Dest、無ければ qt.To)、目的地を選んだ船の初期の量子ドライブ (DriveOf) から出す。
    // 2 区間の経路で区間 1 が終わっていれば、その後に取った点だけで、最終の目的地までを出す。QT の途中でない・目的地の位置が無い・点が無ければ null
    private CurrentLocation.QtEtaEstimate? QtEta(GameLogQuantumState q, CurrentLocation.QtState qt)
    {
        if (!QtActive(q) || (qt.Dest ?? qt.To) is not { } dest || q.TargetTime != _qtPointsTargetTime || _qtPoints.Count == 0) return null;
        var from = q.Leg1EndedAt is { } le && le >= q.TargetTime ? le : q.TargetTime!.Value;
        return CurrentLocation.EstimateQtEta(_qtPoints, (from - DateTime.UnixEpoch).TotalSeconds, dest, DriveOf(q.ShipClass));
    }

    // 船のクラスごとの初期の量子ドライブ (GameDataQueryService.InitialQuantumDriveOf。引けた結果と、引けなかった (null) 結果を覚える。DB を読めなければ覚えずに null)
    private readonly Dictionary<string, CurrentLocation.QtDrive?> _qtDrives = new(StringComparer.OrdinalIgnoreCase);
    private CurrentLocation.QtDrive? DriveOf(string? shipClass)
    {
        if (string.IsNullOrWhiteSpace(shipClass)) return null;
        if (_qtDrives.TryGetValue(shipClass, out var known)) return known;
        try
        {
            var db = DetailPanel.DbPath();
            if (!File.Exists(db)) return null;
            using var gq = GameDataQueryService.OpenReadOnly(db);
            var d = gq.InitialQuantumDriveOf(shipClass) is { } x ? new CurrentLocation.QtDrive(x.Name, x.Speed, x.Accel) : null;
            _qtDrives[shipClass] = d;
            return d;
        }
        catch (Exception) { return null; }
    }

    // 到着予定を出す点 (量子ジャンプの目的地を選んでから到着するまでに取った座標と、取った時刻 (UNIX 秒)。古い順) と、その QT の目的地を選んだ時刻。アプリの中だけ (保存しない)
    private readonly List<((double X, double Y, double Z) Pos, double Unix)> _qtPoints = new();
    private DateTime? _qtPointsTargetTime;

    // 移動中・途中で停止の QT で覚えた状態 (ComputeQt)。_qtFixedTargetTime: その QT の目的地を選んだ時刻 (覚えていなければ null)、_qtFixedTree: 覚えたときの ShopTree
    private DateTime? _qtFixedTargetTime;
    private CurrentLocation.QtState? _qtFixed;
    private ShopTree? _qtFixedTree;

    // ［ゲームログ］の追い読みが動いているか (量子ジャンプの状態が渡ってくるか)
    private bool LogRunning() => QuantumStateProvider?.Invoke() != null;

    // 現在位置の鍵 (種類・場所・出発地・位置 (1 m 単位)。取った座標は時刻も。追い読みが動いているか)。今の時刻の自転を使うとき (取った座標でないとき) は
    // 10 秒ごとに変わる値も入れる (自転で回る場所の位置を書き換えるため)。書き換えるかどうかに使う
    private string EffectiveKey(CurrentLocation.EffectivePosition? e, double now) =>
        (e == null ? "" : $"{e.Kind}|{e.Place?.Record}|{e.FromName}|{e.TargetDesc}|{e.NoPos}|{e.Memory}|{Math.Round(e.Pos.X)}|{Math.Round(e.Pos.Y)}|{Math.Round(e.Pos.Z)}|{(e.Kind == CurrentLocation.PositionKind.Taken ? e.Unix : 0)}")
        + $"|{LogRunning()}|{(e == null || e.Kind != CurrentLocation.PositionKind.Taken ? Math.Floor(now / 10) : 0)}"
        + (_tree != null && ComputeQt(_tree) is { } qs ? $"|{qs.Line}|{qs.Target?.Record}|{(qs.From is { } f ? $"{Math.Round(f.X)},{Math.Round(f.Y)}" : "")}" : "|")
        + $"|{QuantumStateProvider?.Invoke()?.ObjectiveMarkers?.LastOrDefault()?.Time:O}";

    // ページを作り直さずに、今の現在位置などをページのスクリプトへ送って書き換える (UpdateJson)。拡大率と表示の中心はページ側でそのまま
    private void PostUpdate(ShopTree tree, CurrentLocation.EffectivePosition? eff, double now)
    {
        try
        {
            _marks = BuildMarks(tree, eff?.Unix ?? now);
            var json = UpdateJson(tree, _pageView!, eff, LogPlaceNode(tree, LandingPlaceProvider?.Invoke()), LogRunning(), now, ComputeQt(tree), QuantumStateProvider?.Invoke(),
                _marks);
            webMap.CoreWebView2.PostWebMessageAsJson(json);
            _effective = eff;
            _effectiveKey = EffectiveKey(eff, now);
        }
        catch (Exception ex) { txtStatus.Text = $"マップを書き換えられませんでした: {ex.Message}"; }
    }

    // Game.log の居場所の名前 (星図の record の短い名前。例 RR_P3_LEO) → StarMapObject.<名前> の場所。引けなければ null
    internal static ShopTreeNode? LogPlaceNode(ShopTree tree, string? name) =>
        string.IsNullOrWhiteSpace(name) ? null
        : tree.NodesByRecord.TryGetValue("Place:StarMapObject." + name.Trim(), out var n) ? n : null;

    private void BringAppToFront()
    {
        if (Window.GetWindow(this) is not { } w) return;
        var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        if (h != IntPtr.Zero) ShowLocationSender.BringToFront(h);
        w.Activate();
    }

    private static DateTime ToLocal(double unix) => DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(unix * 1000)).LocalDateTime;
    // 時刻の文字 (HH:mm:ss)。有限でない・日時で表せない (DateTimeOffset の範囲の前後 1 日を除いた外) 値は、例外にせず「不明」
    private static string LocalTime(double unix) =>
        !double.IsFinite(unix) || unix < LocalTimeMinUnix || unix > LocalTimeMaxUnix ? "不明"
        : ToLocal(unix).ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly double LocalTimeMinUnix = DateTimeOffset.MinValue.ToUnixTimeSeconds() + 86400.0;
    private static readonly double LocalTimeMaxUnix = DateTimeOffset.MaxValue.ToUnixTimeSeconds() - 86400.0;

    // ページの中身の状態 (ページを作るとき (BuildPage) と、ページを作り直さずに書き換えるとき (UpdateJson) の両方で使う):
    //   表示 (view) の星系図ごとの重ねるもの (MapOverlay。自転の角度・現在位置の印・ラベルの距離・中にいる場所)、【現在位置の近く】の中身の HTML (打ち消し済み。無ければ null)。
    // 惑星・衛星の自転の角度は、現在位置 (eff) があればその時刻、無ければ今の時刻 (unix) のもの (データの式 + 補正値)。
    // 現在位置があれば、その星系の図に印 (取った座標の照準・推定位置の白抜きの丸・量子ジャンプ中の矢印) を描き、各場所のラベルの距離を現在位置からの距離にする
    // (量子ジャンプ中は目的地からの距離に「、到着後」を添える)。logPlace は Game.log の居場所 (現在地の決め方)、logRunning は［ゲームログ］の追い読みが動いているか。
    // 現在位置の星系は、eff.System (量子ジャンプの途中に取った座標の星系の名前) があればその星系、無ければいちばん近い場所の星系 (CurrentLocation.Locate)
    // marks は地図のある場所の印 (［マップ］タブの BuildMarks。各星系図の overlay に入れる)。
    // amenityIconUrl は施設のアイコンの URL の作り方 (アイコンを割り当てたときだけ。各星系図の overlay に入れる。null なら施設のアイコンは描かない)
    internal static (List<(ShopTreeNode Star, string Name, MapOverlay Overlay)> Maps, string? NearInner) MapState(ShopTree tree, string view,
        CurrentLocation.EffectivePosition? eff, ShopTreeNode? logPlace, bool logRunning, double unix, CurrentLocation.QtState? qt = null,
        GameLogQuantumState? q = null, MapMarks? marks = null, Func<string, string?>? amenityIconUrl = null)
    {
        CurrentLocation.Result? loc = null;
        CurrentLocation.Whereabouts? where = null;
        MapOverlay? curOverlay = null;
        if (eff != null) unix = eff.Unix;
        if (eff is { NoPos: false })
        {
            // 量子ジャンプの途中に取った座標 (eff.System がある) は、その名前の星系で決める
            var effSystem = eff.System is { Length: > 0 } es ? tree.Systems.FirstOrDefault(s => s.System.Equals(es, StringComparison.OrdinalIgnoreCase)) : null;
            loc = CurrentLocation.Locate(tree, eff.Pos, eff.Unix, SystemNames, null, fresh: false, effSystem);
            if (loc != null)
            {
                var time = LocalTime(eff.Unix);
                switch (eff.Kind)
                {
                    case CurrentLocation.PositionKind.Moving:
                        curOverlay = new MapOverlay(loc.Thetas, eff.Pos, $"移動中 → {eff.Place?.Name}", MapOverlay.KindMoving, eff.From, eff.Pos, "、到着後");
                        break;
                    case CurrentLocation.PositionKind.EstMemory:
                        // 今の Location の番号で覚えた居場所 (場所の中・天体の付近): 白抜きの丸。ラベルの距離はその位置から
                        curOverlay = new MapOverlay(loc.Thetas, eff.Pos,
                            eff.Memory == "near" ? $"推定位置（{eff.Place?.Name} 付近）" : $"推定位置（{eff.Place?.Name}）", MapOverlay.KindEstimated, null, eff.Pos);
                        break;
                    case CurrentLocation.PositionKind.MovingUnknown:
                        // 目的地が引けない量子ジャンプ: 出発地に白抜きの丸 (矢印は出さない)。ラベルの距離は出発地から
                        curOverlay = new MapOverlay(loc.Thetas, eff.Pos, "移動中（目的地不明）", MapOverlay.KindEstimated, null, eff.Pos);
                        break;
                    case CurrentLocation.PositionKind.EstLanding:
                    case CurrentLocation.PositionKind.EstArrived:
                        where = CurrentLocation.WhereaboutsOf(eff.Pos, loc, logPlace);
                        var estName = eff.Place?.Name ?? "";
                        curOverlay = new MapOverlay(loc.Thetas, eff.Pos,
                            eff.Kind == CurrentLocation.PositionKind.EstArrived ? $"推定位置（{estName}、QT 到着地点付近）" : $"推定位置（{estName}）",
                            MapOverlay.KindEstimated, null, eff.Pos);
                        break;
                    default:
                        where = CurrentLocation.WhereaboutsOf(eff.Pos, loc, logPlace);
                        var label = where.Inside ? $"現在位置: {where.Place!.Name} 内 ({time})"
                            : where.Place != null ? $"現在位置: {where.Place.Name} ({time})" : $"現在位置 ({time})";
                        curOverlay = new MapOverlay(loc.Thetas, eff.Pos, label, MapOverlay.KindTaken, null, eff.Pos, "", where.Inside ? where.Place : null);
                        break;
                }
            }
        }
        var maps = new List<(ShopTreeNode, string, MapOverlay)>();
        // 最近の契約の目標を描く星系: 現在位置の星系、無ければ量子ジャンプの線の星系、無ければ位置の無い推定 (覚えた居場所の「付近」「宇宙」) の場所の星系、
        // 無ければ量子ジャンプの目的地の星系 (星図で引けるとき)
        var objectiveSystem = loc?.System ?? qt?.System
            ?? (eff is { Kind: CurrentLocation.PositionKind.EstMemory, NoPos: true, Place: { } memPlace } ? memPlace.Root : null)
            ?? (q?.Target is { } qTarget ? tree.NodeByEntityName(qTarget)?.Root : null);
        foreach (var name in view == ViewAll ? SystemNames : new[] { view })
        {
            var star = tree.Systems.FirstOrDefault(s => s.System.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (star == null) continue;
            var overlay = loc != null && loc.System == star && curOverlay != null ? curOverlay
                : new MapOverlay(CurrentLocation.ThetasAt(tree.Places.Where(pl => pl.Root == star), unix), null, "");
            // 量子ジャンプの線と目的地 (線を描く星系の図だけ)
            if (qt != null && (qt.System ?? qt.Target?.Root) == star)
                overlay = overlay with { QtFrom = qt.From, QtTo = qt.To, QtDest = qt.Target, QtGoal = qt.Goal };
            // 最近の契約の目標 (今いる星系 (objectiveSystem) の図だけ)
            if (objectiveSystem == star)
            {
                var objs = CurrentLocation.RecentObjectives(tree, q, star, null, eff is { NoPos: false } ep ? ep.Pos : null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
                if (objs.Count > 0) overlay = overlay with { Objectives = objs.Select(o => o.Pos).ToList() };
            }
            if (marks != null) overlay = overlay with { Marks = marks };
            if (amenityIconUrl != null) overlay = overlay with { AmenityIconUrl = amenityIconUrl };
            maps.Add((star, name, overlay));
        }
        string? near = null;
        if (loc != null && eff != null) near = NearInnerHtml(tree, loc, eff, where, logRunning);
        else if (eff is { Kind: CurrentLocation.PositionKind.MovingUnknown })
            // 出発地も引けない量子ジャンプ: 印は出さず、一覧の上の行だけ
            near = "<h3>【現在位置の近く】</h3><p class=\"here\">" + WebUtility.HtmlEncode(MovingUnknownLine(eff)) + "</p>";
        else if (eff is { Kind: CurrentLocation.PositionKind.EstMemory, NoPos: true })
            // 「天体の付近」「宇宙」として覚えた Location の番号: 位置は出さず、現在地の行と星系 (覚えた場所の星系。NearInnerHtml の「星系: 」と同じ形) だけ
            near = "<h3>【現在位置の近く】</h3><p class=\"here\">" + WebUtility.HtmlEncode(eff.Memory == "near"
                ? $"現在地: {eff.Place?.Name} 付近（推定: 覚えた居場所）" : "現在地: 宇宙（推定: 覚えた居場所）") + "</p>"
                + (eff.Place is { } memNode ? "<p>星系: " + WebUtility.HtmlEncode(memNode.Root.System) + "</p>" : "");
        else if (eff == null && logRunning)
            // 推定位置なし: 地図に印は出さない
            near = "<h3>【現在位置の近く】</h3><p class=\"here\">現在地: 不明（［現在位置］を押すと座標を取ります）</p>";
        else if (!logRunning) near = "<h3>【現在位置の近く】</h3><p class=\"warn\">ゲームログを読めていないので推定位置は出せません</p>";
        // 量子ジャンプの状態の行 (「QT 目的地 Orbituary: 設定」など)。現在地の行の下 (無ければ見出しの下)
        if (qt != null)
        {
            var qtLine = "<p class=\"qtline\">" + WebUtility.HtmlEncode(qt.Line) + "</p>"
                + (qt is { From: not null, To: not null, FromSource: { } fs } ? "<p class=\"qtfrom\">出発地: " + WebUtility.HtmlEncode(fs) + "</p>" : "");
            if (near == null) near = "<h3>【現在位置の近く】</h3>" + qtLine;
            else
            {
                var hereEnd = near.IndexOf("</p>", near.IndexOf("<p class=\"here\">", StringComparison.Ordinal) is var hi && hi >= 0 ? hi : 0, StringComparison.Ordinal);
                var at = near.Contains("<p class=\"here\">", StringComparison.Ordinal) && hereEnd >= 0 ? hereEnd + 4 : near.IndexOf("</h3>", StringComparison.Ordinal) + 5;
                near = near.Insert(at, qtLine);
            }
        }
        return (maps, near);
    }

    // ページの HTML と、描いた星系図の数。view が ViewAll なら SystemNames の星系図を並べ、星系名ならその 1 つを枠いっぱいに出す。
    // 各星系図の最初の表示は、覚えた表示 (views) があればそれ、無ければ全体。星系名の見出しを図の上に付ける。中身の状態は MapState。
    // 【現在位置の近く】(aside.near) は、中身が無くても隠して置いておく (書き換えで出すため)
    internal static (string Html, int Count) BuildPage(ShopTree tree, string view, IReadOnlyList<MapViewState> views, CurrentLocation.EffectivePosition? eff,
        ShopTreeNode? logPlace, bool logRunning = true, CurrentLocation.QtState? qt = null, GameLogQuantumState? q = null, MapMarks? marks = null,
        Func<string, string?>? amenityIconUrl = null)
    {
        var (maps, near) = MapState(tree, view, eff, logPlace, logRunning, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0, qt, q, marks, amenityIconUrl);
        var body = new StringBuilder();
        body.Append("<div class=\"mapwrap\">");
        body.Append(view == ViewAll ? "<div class=\"maps\">" : "<div class=\"maps single\">");
        var count = 0;
        foreach (var (star, name, overlay) in maps)
        {
            var saved = views.LastOrDefault(v => string.Equals(v.System, name, StringComparison.OrdinalIgnoreCase));
            (double, double, double)? initial = saved != null && saved.W > 0 && double.IsFinite(saved.Cx) && double.IsFinite(saved.Cy) && double.IsFinite(saved.W)
                ? (saved.Cx, saved.Cy, saved.W) : null;
            var svg = GameDataQueryService.SystemMapSvg(star, null, initial, name, overlay);
            if (svg == null) continue;
            body.Append("<section class=\"sys\"><h3>").Append(WebUtility.HtmlEncode(star.System)).Append("</h3>").Append(DetailHtml.MapBlock(svg, marks != null)).Append("</section>");
            count++;
        }
        body.Append("</div>");
        body.Append(near != null ? "<aside class=\"near\">" + near + "</aside>" : "<aside class=\"near\" hidden></aside>");
        body.Append("</div>");
        return (DetailHtml.Page(body.ToString(), MapCss), count);
    }

    // ページを作り直さずに書き換えるデータ (JSON。ページのスクリプトが受けて書き換える。DetailHtml.MapScript の applyUpdate):
    //   { type: "mapupdate", maps: [{ save: 星系名, pts: [{ r: record 名, x, y, l: ラベル }], here: 中にいる場所の record 名 | null,
    //     mk: { k: "taken" | "est" | "moving", x, y, l: ラベル, x1, y1 (矢印の出発地) } | null }], near: 【現在位置の近く】の中身の HTML (打ち消し済み) | null }
    internal static string UpdateJson(ShopTree tree, string view, CurrentLocation.EffectivePosition? eff, ShopTreeNode? logPlace, bool logRunning, double unix,
        CurrentLocation.QtState? qt = null, GameLogQuantumState? q = null, MapMarks? marks = null)
    {
        var (maps, near) = MapState(tree, view, eff, logPlace, logRunning, unix, qt, q, marks);
        var list = maps.Select(m =>
        {
            var mk = GameDataQueryService.MarkerOf(m.Overlay);
            return new
            {
                save = m.Name,
                pts = GameDataQueryService.SystemMapPointStates(m.Star, m.Overlay).Select(p => new { r = p.Record, x = p.X, y = p.Y, l = p.Label }).ToList(),
                here = m.Overlay.InsidePlace?.RecordName,
                mk = mk is { } k ? new { k = k.Kind, x = k.Pos.X, y = k.Pos.Y, l = k.Label, x1 = (k.From ?? k.Pos).X, y1 = (k.From ?? k.Pos).Y } : null,
                qt = m.Overlay is { QtFrom: { } f, QtTo: { } t } ? new { x1 = f.X, y1 = f.Y, x2 = t.X, y2 = t.Y, goal = m.Overlay.QtGoal } : null,
                objs = (m.Overlay.Objectives ?? Array.Empty<(double X, double Y, double Z)>()).Take(GameDataQueryService.ObjectiveSlots).Select(o => new { x = o.X, y = o.Y }).ToList(),
                qtdest = m.Overlay.QtDest?.RecordName,
            };
        }).ToList();
        return JsonSerializer.Serialize(new { type = "mapupdate", maps = list, near });
    }

    // 目的地が引けない量子ジャンプの現在地の行「現在地: 移動中（Monox → 目的地不明: 地表の地点）」
    private static string MovingUnknownLine(CurrentLocation.EffectivePosition eff) => $"現在地: 移動中（{eff.FromName ?? "?"} → {eff.TargetDesc ?? "目的地不明"}）";

    // 【現在位置の近く】の中身: いちばん上に現在地 (大きめの字)、その下に現在位置の出どころ・星系・近くの惑星の自転の向き、
    // 近い順に 10 か所 (場所名 (押すと［詳細］)・種類・距離)。自転の親の向きが分からない場所には「（自転不明）」。量子ジャンプ中は目的地からの距離。
    // 追い読みが動いていなければ「ゲームログを読めていないので推定位置は出せません」を添える
    private static string NearInnerHtml(ShopTree tree, CurrentLocation.Result loc, CurrentLocation.EffectivePosition eff, CurrentLocation.Whereabouts? where, bool logRunning)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        static string D(double m) => GameDataQueryService.FormatDistance(m);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var estimated = eff.Kind is CurrentLocation.PositionKind.EstLanding or CurrentLocation.PositionKind.EstArrived;
        string here;
        if (eff.Kind == CurrentLocation.PositionKind.Moving) here = $"現在地: 移動中（{eff.FromName ?? "?"} → {eff.Place?.Name}）";
        else if (eff.Kind == CurrentLocation.PositionKind.MovingUnknown) here = MovingUnknownLine(eff);
        else if (eff.Kind == CurrentLocation.PositionKind.EstMemory)
            here = eff.Memory == "near" ? $"現在地: {eff.Place?.Name} 付近（推定: 覚えた居場所）" : $"現在地: {eff.Place?.Name} 内（推定: 覚えた居場所）";
        else if (where is { Place: { } wp })
        {
            var src = estimated ? "推定" : where.FromLog ? "Game.log の居場所から" : "いちばん近い場所";
            here = where.Inside
                ? (estimated ? $"現在地: {wp.Name} 内（{src}）" : $"現在地: {wp.Name} 内（中心から {D(where.Distance)}、{src}）")
                : where.FromLog ? $"現在地: {wp.Name}（{src}）" : $"現在地: {wp.Name} 付近（{D(where.Distance)}、{src}）";
        }
        else
        {
            var nearText = where?.Nearest is { } nn ? $"いちばん近い場所: {nn.Name} まで {D(where.NearestDistance)}" : "近くの場所なし";
            here = $"現在地: 宇宙（{nearText}{(estimated ? "、推定" : "")}）";
        }
        // 近くの Planet・Moon の地表からの高さ (「Pyro I 上空 12.3 Km」)。宇宙なら「宇宙」の代わりに出し、ほかは括弧の中に添える。量子ジャンプ中は出さない
        if (eff.Kind is not (CurrentLocation.PositionKind.Moving or CurrentLocation.PositionKind.MovingUnknown)
            && CurrentLocation.AltitudeText(tree, loc.System, eff.Pos) is { } alt)
        {
            if (here.StartsWith("現在地: 宇宙（", StringComparison.Ordinal)) here = "現在地: " + alt + here["現在地: 宇宙".Length..];
            else if (here.EndsWith("）", StringComparison.Ordinal)) here = here[..^1] + "、" + alt + "）";
            else here += "（" + alt + "）";
        }
        var kind = eff.Kind switch
        {
            CurrentLocation.PositionKind.Taken => $"取った座標 ({ToLocal(eff.Unix).ToString("yyyy-MM-dd HH:mm:ss", inv)})",
            CurrentLocation.PositionKind.EstLanding => $"推定（着陸場所 {eff.Place?.Name}）",
            CurrentLocation.PositionKind.EstArrived => $"推定（QT 到着地点付近: {eff.Place?.Name}）",
            CurrentLocation.PositionKind.MovingUnknown => $"移動中（目的地不明。距離は出発地 {eff.FromName} から）",
            CurrentLocation.PositionKind.EstMemory => $"推定（覚えた居場所: {eff.Place?.Name}{(eff.Memory == "near" ? " 付近" : "")}）",
            _ => $"移動中（{eff.Place?.Name} に着いた後の距離）",
        };
        var sb = new StringBuilder();
        sb.Append("<h3>【現在位置の近く】</h3>");
        if (!logRunning) sb.Append("<p class=\"warn\">ゲームログを読めていないので推定位置は出せません</p>");
        sb.Append("<p class=\"here\">").Append(E(here)).Append("</p>");
        sb.Append("<p>現在位置: ").Append(E(kind)).Append("<br>星系: ").Append(E(loc.System.System));
        if (loc.Planet is { } p)
            sb.Append("<br>").Append(E(SpinLine(p, eff.Unix)));
        sb.Append("</p><table><thead><tr><th>場所</th><th>種類</th><th>距離</th></tr></thead><tbody>");
        foreach (var r in loc.Near)
        {
            sb.Append("<tr><td><a href=\"scjt://detail/").Append(Uri.EscapeDataString(r.Node.Record)).Append("\">").Append(E(r.Node.Name)).Append("</a>");
            if (r.SpinUnknown) sb.Append("（自転不明）");
            sb.Append("</td><td>").Append(E(r.Node.TypeLabel)).Append("</td><td>").Append(E(D(r.Distance)))
              .Append(eff.Kind == CurrentLocation.PositionKind.Moving ? "（到着後）" : "").Append("</td></tr>");
        }
        sb.Append("</tbody></table>");
        return sb.ToString();
    }

    // 近くの惑星の自転の向きの 1 行: 「Bloom の自転の向き: 223.27°（データ 223.56°、補正 -0.29°、2026-10-21 18:54 に Game.log の居場所 Orbituary から補正）」。
    // 補正が無ければ「…: 223.56°（データから（補正なし））」、回らない天体は「…: 回転なし（データ）」、rot_hours が読めていなければ「…: 不明（自転のデータなし）」
    internal static string SpinLine(ShopTreeNode body, double unix)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        static string Deg(double rad) => (rad * 180 / Math.PI).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        var head = $"{body.Name} の自転の向き: ";
        if (body.RotHours == null) return head + "不明（自転のデータなし）";
        if (CurrentLocation.DataThetaAt(body, unix) is not { } data) return head + "回転なし（データ）";
        var list = App.Config.MapSpinCorrections;   // 補正値のリストはこの時点のものを 1 回だけ読んで使う
        var use = CurrentLocation.ThetaAt(body, unix, list) ?? data;
        if (CurrentLocation.CorrectionOf(body, list) is not { } c) return head + $"{Deg(data)}°（データから（補正なし））";
        var from = c.Ref == CurrentLocation.RefLog ? $"Game.log の居場所 {c.RefName}" : $"近くの場所 {c.RefName}";
        var when = ToLocal(c.Utc).ToString("yyyy-MM-dd HH:mm", inv);
        return head + $"{Deg(use)}°（データ {Deg(data)}°、補正 {c.OffsetDeg.ToString("+0.00;-0.00;0.00", inv)}°、{when} に {from} から補正）";
    }

    // マップタブだけの並べ方: 3 星系は横に並べ (1 つ 420px を目安に、狭ければ折り返す)、1 つのときは窓の高さいっぱいに出す。
    // 【現在位置の近く】は図の右 (狭ければ下)。字は図のラベルと同じ大きさ (22px)
    // ===== 星系図と場所の階層マップ =====
    // (2026-11-01、利用者の指示と決めた案 1。クラスの頭の説明を参照)

    // 星系図 → 場所の階層マップ、場所の階層マップ → 星系図 のクロスフェードの時間 (ms)
    private const int FadeMs = 400;

    // ［詳細］の［地図表示］などから呼ぶ (アプリにマップタブは 1 つ)
    private static MapPanel? s_instance;
    // 場所の階層マップを出しているか (星系図は隠す)
    // ページのスクリプトのエラー (［マップ］タブの星系図の MapScript) をアプリのログに書く。
    // 書くもの: 場所 (place)・処理 (where)・エラーの文・スタックの先頭 ScriptErrorStackLines 行。
    // 同じ文 (prefix・場所・処理・エラーの文が同じ) は ScriptErrorGap に 1 回だけ書く (続けて出てもログがあふれないように。抑えた数は次に書くときに添える)
    private const int ScriptErrorStackLines = 5;
    private static readonly TimeSpan ScriptErrorGap = TimeSpan.FromSeconds(5);
    private static readonly Dictionary<string, (DateTime At, int Skipped)> s_scriptErrors = new(StringComparer.Ordinal);
    internal static void LogScriptError(string prefix, string place, string where, string message, string stack)
    {
        var key = $"{prefix}\n{place}\n{where}\n{message}";
        var now = DateTime.UtcNow;
        int skipped;
        lock (s_scriptErrors)
        {
            if (s_scriptErrors.TryGetValue(key, out var last) && now - last.At < ScriptErrorGap)
            {
                s_scriptErrors[key] = (last.At, last.Skipped + 1);
                return;
            }
            skipped = last.Skipped;
            s_scriptErrors[key] = (now, 0);
        }
        var lines = stack.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Take(ScriptErrorStackLines).ToList();
        var sb = new StringBuilder($"{prefix}: 場所 {(place.Length > 0 ? place : "(無し)")}、処理 {(where.Length > 0 ? where : "(不明)")}、{message}");
        if (skipped > 0) sb.Append($" (前に書いてから同じエラーをあと {skipped} 回抑えました)");
        if (lines.Count > 0) sb.Append(" | スタック: ").Append(string.Join(" / ", lines));
        App.AppendStartupLog(App.Config.WorkingDirectory, sb.ToString());
    }


    // 星系図に出している地図のある場所の印 (BuildMarks。RenderAsync・PostUpdate で作り直す)
    private MapMarks? _marks;
    // 区画の置き方の一覧 (view3d_layout.db) が作り直されたら、少し待ってから星系図を作り直す (印を合わせる)
    private readonly System.Windows.Threading.DispatcherTimer _marksTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    // (2026-11-04、利用者の指示「2D 地図は消して」) 場所の地図は、［マップ］タブの中の 2D の地図をやめ、ステーションの 3D (新しい方式) の窓で開く
    private void InitPlaceMap()
    {
        StationView3DLayout.Changed += () => Dispatcher.BeginInvoke(() => { _marksTimer.Stop(); _marksTimer.Start(); });
        _marksTimer.Tick += async (_, _) =>
        {
            _marksTimer.Stop();
            if (_tree == null || _pageView == null) return;
            if (IsVisible) await RenderAsync(); else _stale = true;
        };
    }

    // ［詳細］の［地図表示］(DetailPanel): その場所の 3D (新しい方式) の窓を開く
    public static void ShowPlaceInMapTab(string recordName, string name) => OpenPlace(recordName, name, null);

    // ［詳細］の店の［MAP］(DetailPanel): その店のある場所の 3D の窓を開き、その店を行き先にする
    public static void ShowShopInMapTab(string recordName, string name, ShopTreeShop shop) => OpenPlace(recordName, name, shop.EntityName);

    // 場所の 3D を出す。店の名前・販売品は［詳細］と同じ ShopTree で引く (作れなければ無しで出す)
    // (2026-11-04、利用者の指示「別ウィンドウひらいてたらそっちに…そうでなければ map に出して」) 入口は StationView3DShops.Show と同じ決まり:
    //   別ウィンドウが開いていればそこに出す。無ければ［マップ］タブに切り替えて、その場所の 3D をタブの中 (pnlPlace) に出す
    private static void OpenPlace(string recordName, string name, string? focusShop)
    {
        if (!ShowPlaceInTab(recordName, name, null, focusShop))
            StationView3DShops.Open(recordName, name, null, focusShop, Application.Current?.MainWindow);
    }

    // StationView3DShops.Show から。［マップ］タブがあれば、開いている別ウィンドウか［マップ］タブに出して true。［マップ］タブがまだ無い (作る前) ときは、
    // 別ウィンドウが開いていればそこに出して true、無ければ false (呼んだ側が別ウィンドウで開く)
    internal static bool ShowPlaceInTab(string recordName, string name, ShopTree? tree, string? focusShop)
    {
        var p = s_instance;
        if (p == null) return StationView3DShops.ShowInOpenWindow(recordName, name, tree, focusShop);
        p.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => _ = p.RoutePlaceAsync(recordName, name, tree, focusShop));
        return true;
    }

    private async Task RoutePlaceAsync(string recordName, string name, ShopTree? tree, string? focusShop)
    {
        tree ??= _tree;
        if (tree == null)
        {
            try { tree = await GetTreeAsync(); }
            catch (Exception ex) { App.AppendStartupLog(App.Config.WorkingDirectory, $"[マップ] 「{name}」の 3D に渡す店の表を作れませんでした: {ex.Message}"); }
        }
        if (StationView3DShops.ShowInOpenWindow(recordName, name, tree, focusShop)) return;
        for (DependencyObject? d = this; d != null; d = LogicalTreeHelper.GetParent(d))
            if (d is TabItem tab) { tab.IsSelected = true; break; }
        ShowPlace3D(recordName, name, tree, focusShop);
    }

    // タブの中の 3D (StationView3DView)。同じ場所で店の指定が無ければ、出していたものをそのまま出す (星系図に戻ると閉じるので、次は読み直す)
    private StationView3DView? _place3D;
    private string _place3DName = "";
    private ShopTree? _place3DTree;
    private void ShowPlace3D(string recordName, string name, ShopTree? tree, string? focusShop)
    {
        if (_place3D == null || _place3D.Record != recordName || focusShop != null)
        {
            _place3D?.Close3D();
            Func<string, string, string>? label = tree == null ? null : (g, e) => StationView3DShops.ShopLabel(tree, recordName, g, e);
            Func<string, string, object?>? sales = tree == null ? null : (g, e) => StationView3DShops.Sales(tree, recordName, g, e);
            var view = new StationView3DView(recordName, name, label, sales, focusShop) { InTab = true };
            // 3D から遠ざかり切ったら (ビューアの {type:'leave'}) 星系図に戻る。戻る前に別の場所に替わっていたら何もしない
            view.LeaveRequested += () => Dispatcher.BeginInvoke(() => { if (ReferenceEquals(_place3D, view)) BackToStarMap(); });
            _place3D = view;
            _place3DName = name;
            _place3DTree = tree;
            placeHost.Content = _place3D;
        }
        txtPlaceName.Text = _place3D.PlaceTitle;
        webMap.Visibility = Visibility.Collapsed;
        pnlPlace.Visibility = Visibility.Visible;
    }

    // 星系図を出す。［星系図に戻る］・3D から遠ざかり切ったとき。
    // (2026-11-04、利用者の指示「星系に切り替えたときも3D のメモリ後始末」) タブの中の 3D は隠さずに閉じて、メモリを返す
    private void BackToStarMap()
    {
        if (_place3D is { } v)
        {
            _place3D = null;
            placeHost.Content = null;
            v.Close3D();
        }
        pnlPlace.Visibility = Visibility.Collapsed;
        webMap.Visibility = Visibility.Visible;
    }
    private void BtnPlaceBack_Click(object sender, RoutedEventArgs e) => BackToStarMap();

    // ［別ウィンドウ］: 同じ場所の 3D を別のウィンドウで開く。3D を 2 つ持たないよう、タブの中の 3D は閉じて星系図に戻す
    private void BtnPlaceWindow_Click(object sender, RoutedEventArgs e)
    {
        if (_place3D is not { } v) return;
        var record = v.Record; var name = _place3DName; var tree = _place3DTree;
        _place3D = null;
        placeHost.Content = null;
        v.Close3D();
        BackToStarMap();
        StationView3DShops.Open(record, name, tree, null, Window.GetWindow(this));
    }

    // 星系図の印・ラベルを押した (scjt://detail/… のリンク)。地図のある場所なら、その場所の 3D の窓を開く (2026-11-01、利用者の決定「A 情報ボタンを付ける　押すと詳細に。」)。
    // 地図の無い場所は今までどおり［詳細］タブで開く。押したときの動きを決めるのはここ 1 か所
    private void OnMarkerClicked(string record)
    {
        if (MapPlaceOf(record) is { } mp) { OpenPlace(mp.Record, mp.Name, null); return; }
        if (record.StartsWith(StationPlace.UnlistedPrefix, StringComparison.Ordinal)) return;   // 星図に載っていない場所には詳細が無い
        OpenDetail(record);
    }

    // リンクの record (詳細の record (Place:… など) か、星図に載っていない場所の Unlisted.…) → 地図のある場所 (record 名・名前)。無ければ null
    private (string Record, string Name)? MapPlaceOf(string record)
    {
        if (_tree != null && _tree.NodesByRecord.TryGetValue(record, out var node)) return MapPlaceByRecordName(node.RecordName);
        return MapPlaceByRecordName(record);
    }

    // record 名 (StarMapObject.…) → 地図のある場所 (印を出している場所だけ)。無ければ null
    private (string Record, string Name)? MapPlaceByRecordName(string recordName)
    {
        var marks = _marks;
        if (marks == null) return null;
        if (marks.Has.ContainsKey(recordName) && _tree?.NodeByStarmapRecord(recordName) is { } n) return (n.RecordName, n.Name);
        return null;
    }

    // 星系図の地図のある場所の印: 区画の置き方の一覧 (view3d_layout.db) にある星図の場所で、座標のあるもの (3D の窓で開ける場所)。
    // 印は全部「取り込み済み」(実線)。星図に載っていない場所 (Unlisted.…) の印は出さない (位置を出すのに場所のルートが要るため。2D の地図の調べと一緒にやめた)
    private static MapMarks BuildMarks(ShopTree tree, double unix)
    {
        var inDb = AmenityMapHints.All();
        var has = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var n in tree.Places)
            if (n.Position.HasValue && inDb.ContainsKey(n.RecordName)) has[n.RecordName] = true;
        return new MapMarks(has, new List<(string Record, string Name, string System, (double X, double Y, double Z) Pos, bool Imported)>());
    }

    // ページを作った後 (前は 2D の地図の調べと、現在位置の場所の地図を出す判断をしていた。3D の窓は自分で開かないので、何もしない)
    private void AfterRender(ShopTree tree) { }

    // 場所の今の位置 (星系図に描く位置) と星系。星図の場所は CurrentLocation.PlacePosition (今の自転の角度)。無ければ null
    private (string System, double X, double Y)? PlacePositionOf(ShopTree tree, string recordName)
    {
        if (tree.NodeByStarmapRecord(recordName) is { Position: not null } n)
        {
            var p = CurrentLocation.PlacePosition(n, CurrentLocation.ThetasAt(tree.Places.Where(pl => pl.Root == n.Root), _effective?.Unix ?? Now()));
            return (n.Root.System, p.X, p.Y);
        }
        return null;
    }

    // ページを作り直した (NavigateToString) 後、読み込みが終わるまで待つ (作り直した直後に送る知らせが届かないため。最大 5 秒)
    private TaskCompletionSource? _navDone;
    private async Task WaitNavigatedAsync()
    {
        if (_navDone is { } t) await Task.WhenAny(t.Task, Task.Delay(5000));
    }

    // 星系図のその位置を真ん中に、拡大の上限で出す (MapScript の mapfocus)
    private void FocusStar(string system, double x, double y)
    {
        if (!_webReady || webMap.CoreWebView2 == null) return;
        var save = SystemNames.FirstOrDefault(n => n.Equals(system, StringComparison.OrdinalIgnoreCase)) ?? system;
        try { webMap.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "mapfocus", save, x, y })); }
        catch (Exception ex) { App.AppendStartupLog(App.Config.WorkingDirectory, $"[マップ] 星系図の表示を変えられませんでした: {ex.Message}"); }
    }

    // ===== 場所の検索 =====
    // 候補: 星図の場所 (名前の一部。座標のある場所か地図のある場所) と、星図に載っていない場所 (調べが済んでいれば)。名前の頭が合うものを先に、最大 SearchMax 件。
    // 施設 (cmbAmenity) を選んでいれば、星図の施設の一覧にその施設のある星図の場所だけ (星図に載っていない場所は施設の一覧が無いので入れない)。
    // 施設を選んでいて名前が空なら、その施設のある場所を全部 (最大 SearchMaxAmenity 件)
    private const int SearchMax = 20, SearchMaxAmenity = 100;
    private sealed record SearchItem(string Record, string Name, string Display, bool HasMap);
    // 施設の選択肢 (Id = 施設の種類のレコードの GUID。空は「施設: すべて」)
    private sealed record AmenityChoice(string Id, string Display);

    // 施設の選択肢を、木のどこかの場所にある施設の種類で作り直す (選んでいた施設があれば選んだまま)
    private void RefreshAmenityFilter(ShopTree? tree)
    {
        var keep = (cmbAmenity.SelectedItem as AmenityChoice)?.Id ?? "";
        var list = new List<AmenityChoice> { new("", "施設: すべて") };
        if (tree != null) list.AddRange(tree.AmenityTypes.Select(a => new AmenityChoice(a.Id, a.Label)));
        _amenityRefreshing = true;
        try
        {
            cmbAmenity.ItemsSource = list;
            cmbAmenity.SelectedItem = list.FirstOrDefault(c => c.Id == keep) ?? list[0];
            cmbAmenity.IsEnabled = list.Count > 1;
        }
        finally { _amenityRefreshing = false; }
    }
    private bool _amenityRefreshing;

    // 取り込み済みの地図のデータからの施設の手がかり (record 名 → 手がかり。AmenityMapHints.All。星系図を作り直すたびに裏で読み直す。
    // 同じファイルは覚えたものを使うので、2 回目からは速い)。読めるまでは空 (施設の検索は施設の一覧だけで当てる)
    private Dictionary<string, AmenityMapHints.Hints> _mapHints = new(StringComparer.Ordinal);
    private int _mapHintsVersion;
    private async void RefreshMapHints()
    {
        var version = ++_mapHintsVersion;
        try
        {
            var hints = await Task.Run(AmenityMapHints.All);
            if (version != _mapHintsVersion) return;
            _mapHints = hints;
            if (popSearch.IsOpen && (cmbAmenity.SelectedItem as AmenityChoice)?.Id is { Length: > 0 }) UpdateSearch();
        }
        catch (Exception ex) { App.AppendStartupLog(App.Config.WorkingDirectory, $"[マップ] 地図の施設の手がかりを読めませんでした: {ex.Message}"); }
    }

    private void CmbAmenity_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_amenityRefreshing) return;
        UpdateSearch();
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e) => UpdateSearch();

    private void UpdateSearch()
    {
        var q = txtSearch.Text.Trim();
        var amenity = (cmbAmenity.SelectedItem as AmenityChoice)?.Id ?? "";
        if (q.Length == 0 && amenity.Length == 0 || _tree == null) { popSearch.IsOpen = false; return; }
        var marks = _marks;
        var items = new List<SearchItem>();
        foreach (var n in _tree.Places)
        {
            if (q.Length > 0 && !n.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            // 施設: 星図の施設の一覧にあるか、無くても取り込み済みの地図のデータに手がかりがあれば (精製所・積載ドックだけ。候補に「（推測）」を付ける)
            var guessed = false;
            if (amenity.Length > 0 && !n.Amenities.Any(a => string.Equals(a.Id, amenity, StringComparison.OrdinalIgnoreCase)))
            {
                if (!GameDataQueryService.GuessedByMap(_mapHints.GetValueOrDefault(n.RecordName), amenity)) continue;
                guessed = true;
            }
            var hasMap = marks?.Has.ContainsKey(n.RecordName) == true;
            if (!hasMap && !n.Position.HasValue) continue;
            items.Add(new SearchItem(n.RecordName, n.Name, $"{n.Name}（{n.TypeLabel}・{n.Root.System}）{(hasMap ? "［地図］" : "")}{(guessed ? "（推測）" : "")}", hasMap));
        }
        if (marks != null && amenity.Length == 0)
            foreach (var u in marks.Unlisted.Where(u => u.Name.Contains(q, StringComparison.OrdinalIgnoreCase)))
                items.Add(new SearchItem(u.Record, u.Name, $"{u.Name}（{u.System}）［地図］", true));
        var list = items.OrderBy(i => q.Length > 0 && i.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Take(amenity.Length > 0 ? SearchMaxAmenity : SearchMax).ToList();
        lstSearch.ItemsSource = list;
        popSearch.IsOpen = list.Count > 0;
        if (list.Count == 0 && amenity.Length > 0) txtStatus.Text = "その施設のある場所は見つかりませんでした";
    }

    private void TxtSearch_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && popSearch.IsOpen && lstSearch.Items.Count > 0)
        {
            lstSearch.SelectedIndex = 0;
            (lstSearch.ItemContainerGenerator.ContainerFromIndex(0) as UIElement)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && popSearch.IsOpen && lstSearch.Items.Count > 0)
        {
            PickSearch(lstSearch.Items[0] as SearchItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape) popSearch.IsOpen = false;
    }

    private void LstSearch_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is SearchItem it) PickSearch(it);
    }

    private void LstSearch_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { PickSearch(lstSearch.SelectedItem as SearchItem); e.Handled = true; }
        else if (e.Key == Key.Escape) { popSearch.IsOpen = false; txtSearch.Focus(); e.Handled = true; }
    }

    // 候補を選んだ: 地図のある場所はその階層マップ、ほかは星系図でその位置を真ん中に出す
    private void PickSearch(SearchItem? it)
    {
        popSearch.IsOpen = false;
        if (it == null || _tree == null) return;
        if (it.HasMap) { OpenPlace(it.Record, it.Name, null); return; }
        _ = ShowStarAtAsync(it.Record);
    }

    private async Task ShowStarAtAsync(string recordName)
    {
        if (_tree == null || PlacePositionOf(_tree, recordName) is not { } at) return;
        var view = CurrentView();
        if (view != ViewAll && !string.Equals(view, at.System, StringComparison.OrdinalIgnoreCase))
        {
            App.Config.MapView = SystemNames.FirstOrDefault(n => n.Equals(at.System, StringComparison.OrdinalIgnoreCase)) ?? ViewAll;
            SaveConfig?.Invoke();
            await RenderAsync();
            await WaitNavigatedAsync();
        }
        FocusStar(at.System, at.X, at.Y);
    }

    private const string MapCss = """
        .mapwrap { display: flex; flex-wrap: wrap; gap: 12px; align-items: flex-start; }
        .mapwrap > .maps { flex: 1 1 600px; min-width: 0; }
        .maps { display: flex; flex-wrap: wrap; gap: 12px; }
        .maps .sys { flex: 1 1 420px; min-width: 320px; }
        .maps h3 { margin: 0.2em 0 0.3em; }
        .maps.single .sys { flex-basis: 100%; }
        .maps.single .map svg.zmap { height: calc(100vh - 70px); min-height: 300px; }
        aside.near { flex: 0 1 560px; font-size: 22px; }
        aside.near h3 { font-size: 22px; margin: 0.2em 0 0.3em; }
        aside.near p { margin: 0.2em 0 0.5em; }
        aside.near table { font-size: 22px; }
        aside.near p.here { font-size: 28px; font-weight: 600; color: #B71C1C; margin: 0.2em 0 0.4em; }
        aside.near p.warn { color: #B71C1C; }
        aside.near p.qtline { font-size: 24px; font-weight: 600; color: #C62828; margin: 0 0 0.1em; }
        aside.near p.qtfrom { font-size: 18px; color: #6D4C41; margin: 0 0 0.4em; }
        .map g.mapmark rect { fill: none; stroke: #00897B; stroke-width: 2; }
        .map g.mapmark.todo rect { stroke-dasharray: 3 2; }
        .map g.mapmark { pointer-events: visible; }
        """;
}
