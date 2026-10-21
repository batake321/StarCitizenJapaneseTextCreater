using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
//   図の惑星・衛星の自転の角度は、現在位置があればその取得時刻、無ければ今の時刻のもの
//   タブを初めて開いたとき (Loaded) に作る
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
                    Dispatcher.BeginInvoke(() => OpenDetail(record));
                    return;
                }
                if (e.IsUserInitiated) e.Cancel = true;
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.WebMessageReceived += (_, e) => OnMapMessage(e.WebMessageAsJson);
            webMap.DefaultBackgroundColor = System.Drawing.Color.White;
            _webReady = true;
        }
        catch (Exception ex)
        {
            // WebView2 が使えない (ランタイムが無い等) ときは、図の代わりに断り書きを出す
            _webFailed = true;
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[マップ] WebView2 の初期化に失敗しました: {ex.GetType().FullName} (HResult 0x{ex.HResult:X8}): {ex}");
        }
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
            txtStatus.Text = statusAfter ?? "";
            if (tree == null || tree.IsEmpty) { ShowMessage("インデックスが未構築です (設定タブの［インデックス構築］)"); return; }
            if (_webFailed || !_webReady) { ShowMessage("（図は WebView2 が必要です）"); return; }
            var eff = ComputeEffective(tree);
            _effective = eff;
            _effectiveKey = EffectiveKey(eff, Now());
            var view = CurrentView();
            var (html, count) = BuildPage(tree, view, App.Config.MapViews, eff, LogPlaceNode(tree, LandingPlaceProvider?.Invoke()), LogRunning(), ComputeQt(tree),
                QuantumStateProvider?.Invoke());
            if (count == 0) { ShowMessage("座標のある星系がありません"); _pageView = null; return; }
            txtMessage.Visibility = Visibility.Collapsed;
            webMap.Visibility = Visibility.Visible;
            webMap.CoreWebView2.NavigateToString(html);
            _pageView = view;
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

    // ［現在位置］: ゲームに /showlocation を送って座標を読み、現在位置の星系 1 つの表示にする (CaptureAsync。自動の取得と同時には動かない)
    private async void BtnCurrent_Click(object sender, RoutedEventArgs e) => await CaptureAsync(auto: false);

    // 座標を取っている途中か (［現在位置］と自動の取得を同時に動かさない)、最後に自動で取った時刻 (UNIX 秒)
    private bool _capturing;
    private double _lastAutoCapture;

    // ［ゲームログ］の追い読みで量子ジャンプの目的地の選択 (kind = "select") か到着 (kind = "arrive") の行を見た (GameLogPanel.QuantumEventSeen)。
    // 「QT の前後で自動で現在位置を取る」がオンなら座標を取る (CaptureAsync(auto: true))。マップタブが見えていなくても動く。
    //   目的地の選択: 行を見たらすぐ (跳ぶ前に出発地を取るため)。取れなかった (座標を読めない・ゲームの窓が前に無い) ら、3 秒おきにあと 2 回まで試す。
    //     量子ジャンプが移動中になったら、その時点でやめる。ボタンの取得の途中なら、その回は飛ばす
    //     (星図 (mobiGlas など) を開いている間はチャットに届かず取れないことがあるため)
    //   到着: 2 秒後に 1 回。
    //   前に自動で取り始めてから 10 秒以内なら始めない (同じ目的地の選び直しが続いたときは最初の 1 回だけ。再試行には当てない)
    public void OnQuantumEvent(string kind)
    {
        if (!App.Config.MapAutoCapture) return;
        if (Now() - _lastAutoCapture < AutoCaptureMinIntervalSeconds) return;
        _lastAutoCapture = Now();
        var select = kind == "select";
        _ = Dispatcher.InvokeAsync(async () =>
        {
            if (!select) await Task.Delay(TimeSpan.FromSeconds(2));   // 目的地の選択はすぐ取りに行く。到着は 2 秒後
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
    // どちらも、取った座標・補正値・Location の番号の覚えは同じ。取っている間はもう片方を動かさない (ボタンは押せない・自動はやめる)
    // 座標を取れたら true
    private async Task<bool> CaptureAsync(bool auto)
    {
        if (_capturing) return false;
        _capturing = true;
        // 取れなかったとき: 画面の知らせ (status) を出し、アプリのログに「[マップ] 現在位置を取れませんでした（自動 / ボタン）: <理由>」を出す (理由は無ければ status)
        var mode = auto ? "自動" : "ボタン";
        // クリップボードを元に戻せなかったときの知らせ (状態の欄の終わりに添える。戻せたときは空)
        var clipboardNote = "";
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
            try
            {
                // ゲームの窓はもう前にある (ボタンは上で前に出し、自動は前にあることを確かめた) ので、ここでは前に出さない
                sent = await Task.Run(() => ShowLocationSender.Send(game, bringToFront: false));
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
                return Fail("座標を読めませんでした (クリップボードの文字が /showlocation の座標の形ではありません)");
            }
            var tree = await GetTreeAsync();
            if (tree == null || tree.IsEmpty) { ShowMessage("インデックスが未構築です (設定タブの［インデックス構築］)"); return Fail("", "インデックスが未構築です"); }
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            var res = CurrentLocation.Locate(tree, pos, now, SystemNames, LogPlaceNode(tree, LandingPlaceProvider?.Invoke()), fresh: true);
            if (res == null) return Fail("現在位置の星系が分かりませんでした");
            // 取れた: アプリのログに座標・星系・いちばん近い場所を出す
            App.AppendStartupLog(App.Config.WorkingDirectory, string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"[マップ] 現在位置を取りました（{mode}）: x{pos.X:0.0} y{pos.Y:0.0} z{pos.Z:0.0}（星系 {res.System.System}、いちばん近い場所 {(res.Near.FirstOrDefault() is { } nr ? $"{nr.Node.Name} {GameDataQueryService.FormatDistance(nr.Distance)}" : "なし")}）"));
            var system = SystemNames.First(n => n.Equals(res.System.System, StringComparison.OrdinalIgnoreCase));
            // 取ったときの Game.log の Location の番号と、そのときの「どこにいたか」を組にして覚える (番号ごとに最新の 1 つ。CurrentLocation.Classify)。
            // 覚えたリストは書き換えず、新しいリストに入れ替える
            var locationId = QuantumStateProvider?.Invoke()?.LocationId ?? "";
            App.Config.MapCurrent = new MapCurrentState { X = pos.X, Y = pos.Y, Z = pos.Z, Utc = now, LocationId = locationId };
            if (locationId.Length > 0 && CurrentLocation.Classify(tree, pos, now, SystemNames, LogPlaceNode(tree, LandingPlaceProvider?.Invoke())) is { } where)
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
            await RenderAsync($"現在位置を取得しました ({LocalTime(now)})" + ocrNote + clipboardNote);
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

    // 量子ジャンプの状態 (CurrentLocation.Quantum。追い読みが動いていなければ null)
    private CurrentLocation.QtState? ComputeQt(ShopTree tree)
    {
        var q = QuantumStateProvider?.Invoke();
        return q == null ? null : CurrentLocation.Quantum(tree, q, App.Config.MapCurrent, Now(), LandingPlaceProvider?.Invoke(), SystemNames);
    }

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
            var json = UpdateJson(tree, _pageView!, eff, LogPlaceNode(tree, LandingPlaceProvider?.Invoke()), LogRunning(), now, ComputeQt(tree), QuantumStateProvider?.Invoke());
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
    private static string LocalTime(double unix) => ToLocal(unix).ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    // ページの中身の状態 (ページを作るとき (BuildPage) と、ページを作り直さずに書き換えるとき (UpdateJson) の両方で使う):
    //   表示 (view) の星系図ごとの重ねるもの (MapOverlay。自転の角度・現在位置の印・ラベルの距離・中にいる場所)、【現在位置の近く】の中身の HTML (打ち消し済み。無ければ null)。
    // 惑星・衛星の自転の角度は、現在位置 (eff) があればその時刻、無ければ今の時刻 (unix) のもの (データの式 + 補正値)。
    // 現在位置があれば、その星系の図に印 (取った座標の照準・推定位置の白抜きの丸・量子ジャンプ中の矢印) を描き、各場所のラベルの距離を現在位置からの距離にする
    // (量子ジャンプ中は目的地からの距離に「、到着後」を添える)。logPlace は Game.log の居場所 (現在地の決め方)、logRunning は［ゲームログ］の追い読みが動いているか
    internal static (List<(ShopTreeNode Star, string Name, MapOverlay Overlay)> Maps, string? NearInner) MapState(ShopTree tree, string view,
        CurrentLocation.EffectivePosition? eff, ShopTreeNode? logPlace, bool logRunning, double unix, CurrentLocation.QtState? qt = null,
        GameLogQuantumState? q = null)
    {
        CurrentLocation.Result? loc = null;
        CurrentLocation.Whereabouts? where = null;
        MapOverlay? curOverlay = null;
        if (eff != null) unix = eff.Unix;
        if (eff is { NoPos: false })
        {
            loc = CurrentLocation.Locate(tree, eff.Pos, eff.Unix, SystemNames, null, fresh: false);
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
        foreach (var name in view == ViewAll ? SystemNames : new[] { view })
        {
            var star = tree.Systems.FirstOrDefault(s => s.System.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (star == null) continue;
            var overlay = loc != null && loc.System == star && curOverlay != null ? curOverlay
                : new MapOverlay(CurrentLocation.ThetasAt(tree.Places.Where(pl => pl.Root == star), unix), null, "");
            // 量子ジャンプの線と目的地 (線を描く星系の図だけ)
            if (qt != null && (qt.System ?? qt.Target?.Root) == star)
                overlay = overlay with { QtFrom = qt.From, QtTo = qt.To, QtDest = qt.Target, QtGoal = qt.Goal };
            // 最近の契約の目標 (今いる星系 (現在位置の星系、無ければ量子ジャンプの線の星系) の図だけ)
            if ((loc?.System ?? qt?.System) == star)
            {
                var objs = CurrentLocation.RecentObjectives(tree, q, star, null, eff is { NoPos: false } ep ? ep.Pos : null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
                if (objs.Count > 0) overlay = overlay with { Objectives = objs.Select(o => o.Pos).ToList() };
            }
            maps.Add((star, name, overlay));
        }
        string? near = null;
        if (loc != null && eff != null) near = NearInnerHtml(tree, loc, eff, where, logRunning);
        else if (eff is { Kind: CurrentLocation.PositionKind.MovingUnknown })
            // 出発地も引けない量子ジャンプ: 印は出さず、一覧の上の行だけ
            near = "<h3>【現在位置の近く】</h3><p class=\"here\">" + WebUtility.HtmlEncode(MovingUnknownLine(eff)) + "</p>";
        else if (eff is { Kind: CurrentLocation.PositionKind.EstMemory, NoPos: true })
            // 「天体の付近」「宇宙」として覚えた Location の番号: 位置は出さず、現在地の行だけ
            near = "<h3>【現在位置の近く】</h3><p class=\"here\">" + WebUtility.HtmlEncode(eff.Memory == "near"
                ? $"現在地: {eff.Place?.Name} 付近（推定: 覚えた居場所）" : "現在地: 宇宙（推定: 覚えた居場所）") + "</p>";
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
        ShopTreeNode? logPlace, bool logRunning = true, CurrentLocation.QtState? qt = null, GameLogQuantumState? q = null)
    {
        var (maps, near) = MapState(tree, view, eff, logPlace, logRunning, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0, qt, q);
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
            body.Append("<section class=\"sys\"><h3>").Append(WebUtility.HtmlEncode(star.System)).Append("</h3>").Append(DetailHtml.MapBlock(svg)).Append("</section>");
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
        CurrentLocation.QtState? qt = null, GameLogQuantumState? q = null)
    {
        var (maps, near) = MapState(tree, view, eff, logPlace, logRunning, unix, qt, q);
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
        """;
}
