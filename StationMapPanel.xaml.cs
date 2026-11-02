using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

// ステーションの階層マップの欄 (［詳細］の欄の中と、別ウィンドウ StationMapWindow で使う)。
//   地図は StationMapWeb のビューア (2D。斜めに重ねた案内図と、この階だけ (真上から))。保存した JSON は StationMapExports.GetCachedPathAsync のもの
//   (Data.p4k が変わっていれば照合してから)。出している間は StationMapExports.AcquireView で「表示中」にする。
//   名前: 店は［詳細］のお店の木 (ShopTree) の名前 (shop_id の 3 番目 = SCShop の CryGUID で引く。無ければ Entity 名から SCShop_ を外したもの)。
//     階の名前 (@…_Floor_NN など) はゲームの文 (作業ディレクトリの global.ini。日本語、無ければ英語)、区画の名札・乗り場の行き先は英語 (地名は英語のまま)。
//     階・区画の名前は画面で作らない (引けないキーは @ を外したデータの名前、名札の無い区画はデータの区画の名前、引けない行き先は付けない)
//   現在位置 (赤い点): App.Config.MapCurrent を、保存した JSON から組み直した StationRoot の ToLocal で局所の座標に直す。都市 (自転する天体の下) は、
//     取った時刻 (秒未満まで) の StationRoot.CorrectedTheta (データの式 + 補正値) を使う。前の版の JSON (その値が無い) なら取り込み直しが要ると出す。
//     区画の箱に入らなければ「ステーションの外です（いちばん近いステーション・距離）」、全部の区画の範囲の中なら「区画の外（ハンガーなど）」とし、
//     道順はいちばん近い到着口 (行き先の名前に docking・hangar のあるエレベーター) から出す
//   道順: StationRouteGraph の関数 (StationMapModel.Route) で計算し、ビューアに線を、左に手順を出す
public partial class StationMapPanel : UserControl
{
    // 階の一覧の行: 階 1 つ (Id あり。Name は階の表示の名前)、または同じ名前 (数字だけ違う名前も) の階のまとまりの行 (IsGroup。Id は ""、Group はまとまりの鍵 = FloorGroupKey)。
    // まとまりの中の行は Group にまとまりの鍵が入る
    private sealed record FloorRow(string Id, string Name, string Display, string? Group = null, bool IsGroup = false);
    private sealed record SectionRow(string Id, string Display);
    // 端末の一覧の 1 行 (端末のまとまり StationMapModel.TerminalGroups の番号)
    private sealed record TerminalRow(int Group, string Display);
    // 出発地・目的地の 1 行。Kind: current・pin・shop・section・transit・terminal (Index は端末のまとまりの番号)
    private sealed record Place(string Kind, int Index, string Id, string Display);

    private StationMapModel? _model;
    private string _record = "";
    private string _name = "";
    private ShopTree? _tree;
    private Task? _webInit;
    private bool _viewerReady;
    private bool _suppress;
    // ビューアから「この階だけ」の状態が来て欄を合わせている間 (変更の知らせを送り返さない)
    private bool _suppressTop;
    private string? _floor;
    private readonly Dictionary<string, string> _floorDisplay = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _floorStepName = new(StringComparer.Ordinal);
    // 一覧に出す階 (重ねる並び (StackOrder) の順。まとめる前の 1 階ずつの行)
    private List<FloorRow> _floorItems = new();
    // 開いている同じ名前の階のまとまりの鍵 (地図ごと。地図を出すたびに全部閉じる)
    private readonly HashSet<string> _openFloorGroups = new(StringComparer.Ordinal);
    private Dictionary<int, string> _shopNames = new();
    // 店の種類 (ShopTreeShop.Kind。装備の店・コモディティの端末など。［詳細］の店の候補の [種類] と同じ。引けなければ無し)
    private Dictionary<int, string> _shopKinds = new();
    // 地図の名札・リストに出す店の名前「店名（種類）」
    private Dictionary<int, string> _shopLabels = new();
    private Dictionary<int, string> _transitNames = new();
    private Dictionary<string, string> _sectionNames = new(StringComparer.Ordinal);
    // 乗り場の行き先の名前 (区画の名前 place か行き先。手順の文の「シャトル（A回り）で ◯」に使う) と、地区の名前
    private Dictionary<int, string> _stopNames = new();
    private Dictionary<string, string> _districtNames = new(StringComparer.Ordinal);
    private List<Place> _places = new();
    private Place? _start, _dest;
    private ((double X, double Y, double Z) Pos, string Floor)? _pin;
    // 現在位置 (局所)。Inside: 区画の箱に入る、Bounds: 区画の範囲の中 (ハンガーなど)、Outside: 外
    private ((double X, double Y, double Z) Pos, string Floor, string State)? _cur;
    private double _curUtc;
    // 自転する天体の record 名 (都市のとき。引けなければ null)
    private string? _spinBody;
    private int _routeVersion;
    // 「表示中」にしている地図の短い名前 (StationMapExports.AcquireView。出していなければ null)
    private string? _viewShort;
    // 窓を閉じた (DisposeWeb の後)。読み込みの途中で閉じたら、その後は表示中の印 (AcquireView) を付けずに抜ける (付けると外す人がいなくなり、
    // 容量の上限で消されない地図が残る)
    private bool _disposed;
    // ビューアへ送る地図 (StationMapModel.ViewerMap)
    private string? _viewerMap;
    private readonly DispatcherTimer _curTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    private const string StartCurrentId = "current", StartPinId = "pin";
    // 地図の表示 (WebView2) の用意を待つ上限 (ShowCoreAsync。終わらないときに「読み込み中...」のままにしないための区切り)
    private static readonly TimeSpan WebViewInitLimit = TimeSpan.FromSeconds(30);

    // 地図の WebView2 を合成で描く版 (WebView2CompositionControl) にするか。［マップ］タブ (MapPanel) は true (星系図と重ねて透明度を変える (クロスフェード) ため。
    // HWND の WebView2 は WPF の透明度が効かない)。別ウィンドウも true (2026-11-02。同じ利用者データの置き場で HWND と合成が混ざると初期化が 0x8007139F で失敗したため、
    // 種類をそろえた。StationMapWeb.InitAsync の説明)。XAML で設定し、最初に地図を出す前に決める
    public bool UseComposition { get; set; }
    private Microsoft.Web.WebView2.Wpf.IWebView2? _web;
    // 地図の WebView2 (初めて使うときに作って webHost に入れる)
    private Microsoft.Web.WebView2.Wpf.IWebView2 Web
    {
        get
        {
            if (_web != null) return _web;
            _web = UseComposition ? new Microsoft.Web.WebView2.Wpf.WebView2CompositionControl() : new Microsoft.Web.WebView2.Wpf.WebView2();
            webHost.Content = _web;
            return _web;
        }
    }
    private FrameworkElement WebElement => (FrameworkElement)Web;

    // ビューアで縮小して、描いている図の全体が小さくなった (viewer.js の zoomout の知らせ)。［マップ］タブ (MapPanel) が星系図へ戻るのに使う。別ウィンドウは使わない
    public event Action? ZoomedOut;

    public StationMapPanel()
    {
        InitializeComponent();
        // 現在位置が入れ替わったら (マップタブの［現在位置］・自動の取得) 出し直す
        // どの画面の［現在位置］のボタン (［マップ］タブ・ステーションの地図) で取ったときも、出発地を現在位置にする (ApplyButtonCapture)
        // 自動の取得などで現在位置が入れ替わったときは、出発地が「現在位置」なら道順を出し直す (階は移さない。ほかの出発地は変えない)
        _curTimer.Tick += (_, _) =>
        {
            if (_model == null) return;
            var changed = App.Config.MapCurrent.Utc != _curUtc;
            if (changed) UpdateCurrent();
            if (!ApplyButtonCapture() && changed && _start?.Kind == "current") _ = RouteAsync(fit: false);
        };
        Loaded += (_, _) => { _curTimer.Start(); StationMapExports.Instance.Exported += OnExported; };
        Unloaded += (_, _) => { _curTimer.Stop(); StationMapExports.Instance.Exported -= OnExported; };
    }

    // 取り込みが終わった: その場所を「まだ取り込んでいない」と出していたら、そのまま出す
    private void OnExported(string shortName)
    {
        if (_disposed || _model != null || _record.Length == 0 || !StationMapExports.ShortName(_record).Equals(shortName, StringComparison.OrdinalIgnoreCase)) return;
        _ = ShowAsync(_record, _name, _tree);
    }

    // 表示している場所の record (StarMapObject.…。出していなければ "")
    public string Record => _record;

    // その場所の保存した地図を出す。無ければ知らせを出して false
    // 途中の例外は「読み込み中...」のままにせず、理由を画面とアプリのログに出す
    public async Task<bool> ShowAsync(string record, string name, ShopTree? tree)
    {
        if (_disposed) return false;
        try { return await ShowCoreAsync(record, name, tree); }
        catch (Exception ex)
        {
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの地図] 「{name}」の地図を出せませんでした: {ex}");
            Clear($"地図を出せませんでした: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> ShowCoreAsync(string record, string name, ShopTree? tree)
    {
        Clear();
        _record = record;
        _name = name;
        _tree = tree;
        txtTitle.Text = name;
        var ex0 = StationMapExports.Instance;
        var state = ex0.StateOf(record);
        if (state is Ship3DCacheState.NeedsVerify or Ship3DCacheState.Verifying) ShowOverlay("照合中 (パッチで Data.p4k が変わったので、この場所の元データを確かめています)...");
        // 最新の地図 (Data.p4k が変わっていれば照合して同じだったもの) のパス。無ければ null
        var path = await ex0.GetCachedPathAsync(record);
        if (_disposed || record != _record) return false;
        if (path == null)
        {
            ShowOverlay(state == Ship3DCacheState.NoGame ? "ゲームの Data.p4k が見つかりません。設定タブのゲームのフォルダとチャンネルを確かめてください。"
                : ex0.IsExporting(record) ? "取り込み中です。終わったらここに出します。"
                : state is Ship3DCacheState.NeedsVerify or Ship3DCacheState.Verifying ? "この場所の元データがパッチで変わったので、地図を消しました。［読み込み］で取り込み直してください。"
                : $"「{name}」の地図はまだ取り込んでいません。［読み込み］でゲームデータから取り込んでください。");
            return false;
        }
        // 出している間は容量の上限・照合で消されないようにする
        _viewShort = StationMapExports.ShortName(record);
        ex0.AcquireView(_viewShort);
        StationMapModel model;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            ShowOverlay("読み込み中...");
            model = await Task.Run(() => StationMapModel.Load(path));
        }
        catch (StationMapModel.OldFormatException ex)
        {
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの地図] {path}: {ex.Message}");
            Clear(ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの地図] {path} を読めませんでした: {ex}");
            Clear($"地図を読めませんでした: {ex.Message}");
            return false;
        }
        var loadMs = sw.ElapsedMilliseconds;
        if (_disposed || record != _record) return false;
        _model = model;
        _spinBody = null;
        if (model.RootInfo is { Spins: true } sr && sr.SpinUuid.Length > 0)
        {
            try { _spinBody = await Task.Run(() => RecordOfUuid(sr.SpinUuid)); }
            catch (Exception ex) { App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの地図] 自転する天体を星図で引けませんでした: {ex.Message}"); }
            if (_disposed || record != _record) return false;
        }
        await PrepareNamesAsync(model, tree);
        if (_disposed || record != _record) return false;
        // ビューアへ送る地図 (描くのに要るものだけ。道順のグラフは送らない)
        var floorNames = new Dictionary<string, string>(_floorDisplay, StringComparer.Ordinal);
        var shopNames = new Dictionary<int, string>(_shopLabels);   // 地図の名札は「店名（種類）」
        var transitNames = new Dictionary<int, string>(_transitNames);
        var sectionNames = new Dictionary<string, string>(_sectionNames, StringComparer.Ordinal);
        var districtNames = new Dictionary<string, string>(_districtNames, StringComparer.Ordinal);
        _viewerMap = await Task.Run(() => model.ViewerMap(floorNames, shopNames, transitNames, sectionNames, districtNames));
        if (_disposed || record != _record) return false;
        App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの地図] 「{name}」を読みました: 読み込み {loadMs} ms、名前と送る地図まで {sw.ElapsedMilliseconds} ms" +
            $" (節点 {model.Graph.Nodes.Count}、辺 {model.Graph.Edges.Count}、送る地図 {_viewerMap.Length / 1024} KB)");
        _pin = null;
        _start = _dest = null;
        // 地図を開く前にボタンで取った位置は、開いた地図の出発地にはしない (前と同じく、開いたときの出発地は空)
        _startFromButtonUtc = MapPanel.LastButtonCaptureUtc;
        BuildLists();
        UpdateCurrent(send: false);
        // 現在位置がこの場所の中 (区画の中・区画の外でも範囲の中。ApplyButtonCapture の gotoCurrent と同じ判定) なら、初めの階を現在位置の階にする。
        // ステーションの外なら初めの階 (店のいちばん多い階) のまま、現在位置の印も出さない (SendCurrent)
        if (_cur is { State: not "Outside" } cur0 && cur0.Floor.Length > 0) SelectFloorInList(cur0.Floor);
        // 合成で描く WebView2 (WebView2CompositionControl。［マップ］タブ) は、隠したまま (Collapsed) だと EnsureCoreWebView2Async が終わらない
        // (2026-11-02 に小さな試しのアプリで確かめた: Collapsed のままでは 8 秒たっても終わらず、Visible にすると終わる。HWND の WebView2 は Collapsed でも終わる)。
        // 「読み込み中...」の知らせ (ShowOverlay) で隠しているので、用意の前に出す (知らせの文は上に重ねて出たまま。HWND の WebView2 は文の上に出てしまうので今までどおり)
        if (UseComposition) WebElement.Visibility = Visibility.Visible;
        try
        {
            var init = _webInit ??= StationMapWeb.InitAsync(Web, OnViewerMessage, () => $"{_name} ({_record})", OnViewerError);
            // 用意が終わらないときに「読み込み中...」のままにしない (WebViewInitLimit たっても終わらなければ、知らせとログを出してやめる。用意はそのまま続き、次に出すときに使う)
            if (await Task.WhenAny(init, Task.Delay(WebViewInitLimit)) != init)
            {
                if (_disposed || record != _record) return false;
                App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの地図] 「{name}」({record}): 地図の表示 (WebView2) の用意が {WebViewInitLimit.TotalSeconds:0} 秒たっても終わりません");
                ShowOverlay($"地図の表示 (WebView2) の用意が {WebViewInitLimit.TotalSeconds:0} 秒たっても終わりません。もう一度開いてください。");
                return false;
            }
            await init;
        }
        catch (Exception ex)
        {
            _webInit = null;
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの地図] WebView2 の初期化に失敗しました: {ex.GetType().FullName} (HResult 0x{ex.HResult:X8}): {ex}");
            ShowOverlay($"地図の表示 (WebView2) を使えません: {ex.Message}");
            return false;
        }
        txtOverlay.Visibility = Visibility.Collapsed;
        WebElement.Visibility = Visibility.Visible;
        if (_viewerReady) SendLoad();   // まだならビューアの ready で送る
        return true;
    }

    // 表示をやめる (text があれば知らせを出す)
    public void Clear(string text = "")
    {
        _model = null;
        _viewerMap = null;
        _pendingFocus = null;
        _loadWatch.Stop();
        _record = "";
        if (_viewShort != null) { StationMapExports.Instance.ReleaseView(_viewShort); _viewShort = null; }
        _routeVersion++;
        _suppress = true;
        try
        {
            lstFloors.ItemsSource = null; lstSections.ItemsSource = null; lstTerminals.ItemsSource = null; lstStart.ItemsSource = null; lstDest.ItemsSource = null; lstSteps.ItemsSource = null;
        }
        finally { _suppress = false; }
        txtRoute.Text = ""; txtCurrent.Text = "";
        if (_viewerReady) StationMapWeb.Post(Web, "{\"type\":\"load\",\"map\":{\"sections\":[],\"floors\":[]}}");
        ShowOverlay(text);
    }

    public void DisposeWeb()
    {
        _disposed = true;
        _curTimer.Stop();
        if (_viewShort != null) { StationMapExports.Instance.ReleaseView(_viewShort); _viewShort = null; }
        try { (_web as IDisposable)?.Dispose(); } catch { }
    }

    private void ShowOverlay(string text)
    {
        txtOverlay.Text = text;
        txtOverlay.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (text.Length > 0 && _model == null) WebElement.Visibility = Visibility.Collapsed;
    }

    // ===== 名前 =====

    private async Task PrepareNamesAsync(StationMapModel m, ShopTree? tree)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in m.Floors) foreach (var part in f.Name.Split(" / ")) if (part.StartsWith('@')) keys.Add(part);
        foreach (var s in m.Sections) if (s.Label.StartsWith('@')) keys.Add(s.Label);
        foreach (var t in m.Transit) if (t.Dest.StartsWith('@')) keys.Add(t.Dest);
        (Dictionary<string, string> En, Dictionary<string, string> Ja) ini;
        try { ini = await Task.Run(LoadIni); }
        catch (Exception ex)
        {
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの地図] global.ini を読めませんでした: {ex.Message}");
            ini = (new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase));
        }
        // 引けなければ null (キーのままは出さない)
        string? En(string k) => ResolveLoc(k, ini.En);
        string? Ja(string k) => ResolveLoc(k, ini.Ja) ?? ResolveLoc(k, ini.En);

        _shopNames = m.Shops.ToDictionary(s => s.Index, s => ShopName(tree, m.Record, s));
        // 種類 (ShopTreeShop.Kind) は、品目・コモディティの価格が結ばれている店だけ (inventory_type だけでは種類を付けない。食べ物の屋台が「装備の店」になっていた)
        _shopKinds = m.Shops.Select(s => (s.Index, Kind: ShopOf(tree, m.Record, s) is { } sh && (sh.Items.Count > 0 || sh.Commodities.Count > 0) ? sh.Kind : ""))
            .Where(t => t.Kind.Length > 0).ToDictionary(t => t.Index, t => t.Kind);
        // 地図の名札とリストの店の名前: 「店名（種類）」(利用者の決定。店名は ShopTree の名前 (英語のまま)、種類は ShopTreeShop.Kind (装備の店・コモディティの端末など。
        // ［詳細］と同じ言葉))。種類が引けない店・名前と種類が同じ言葉の店は店名だけ。ゲームの名前の無い店 (店名が "") は名札を出さない (リストは ShopNoName)
        _shopLabels = m.Shops.ToDictionary(s => s.Index, s => _shopNames[s.Index] is var nm && nm.Length > 0 && _shopKinds.TryGetValue(s.Index, out var k) && k != nm ? $"{nm}（{k}）" : _shopNames[s.Index]);
        // データの名前の中の店のエンティティ名 (SCShop_…) は、店の名前 (ShopTree・game_shops) に直す
        var shopByEntity = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sh in m.Shops.Where(x => _shopNames[x.Index].Length > 0)) shopByEntity.TryAdd(sh.Entity, _shopNames[sh.Index]);
        if (tree != null)
            foreach (var sh in tree.Shops.Where(x => x.EntityName.Length > 0 && x.Name.Length > 0).OrderByDescending(x => Under(x.Location, m.Record)))
                shopByEntity.TryAdd(sh.EntityName, sh.Name);
        string FixShops(string text) => ShopEntityRx.Replace(text, mt => shopByEntity.TryGetValue(mt.Value, out var n) ? n : mt.Value);

        _floorDisplay.Clear(); _floorStepName.Clear();
        foreach (var f in m.Floors)
        {
            // 階の名前はデータが付けた表示の名前 (displayName)。無い JSON では、鍵をゲームの日本語 (無ければ英語) で引いたもの (引けない鍵は @ を外したデータの名前のまま)
            string name;
            if (f.DisplayName.Length > 0) name = f.DisplayName;
            else
            {
                var parts = f.Name.Split(" / ").Select(p => p.StartsWith('@') ? Ja(p) ?? p[1..] : p).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).ToList();
                name = parts.Count > 0 ? string.Join(" / ", parts) : f.Id;
            }
            _floorDisplay[f.Id] = FixShops(name);
        }
        // 階の名前そのものには高さを添えない (一覧に出る階どうしの同じ名前は、データ側が displayName の括弧で見分けている。
        // 括弧で見分けられない同じ名前の階は、階の一覧で ▼ のまとまりにまとめ、中の行にだけ高さを添える (RefreshFloors・FloorHeight))
        // Describe が「◯階」と付けるので、名前の終わりの「階」は外して渡す
        foreach (var f in m.Floors) _floorStepName[f.Id] = _floorDisplay[f.Id].EndsWith('階') ? _floorDisplay[f.Id][..^1] : _floorDisplay[f.Id];

        // 区画の名札・乗り場の行き先は地名なので英語。引けない名札は区画の Entity 名、引けない行き先は付けない
        // 区画の名前はデータが付けた表示の名前 (displayName。店のエンティティ名は店の名前に直す)。無ければ名札 (英語) か区画の名前
        _sectionNames = m.Sections.ToDictionary(s => s.Id, s => FixShops(s.DisplayName.Length > 0 ? s.DisplayName
            : (s.Label.StartsWith('@') ? En(s.Label) : s.Label.Length > 0 ? s.Label : null) ?? s.Name), StringComparer.Ordinal);
        // 乗り場の名前: データの place (乗り場を含む区画の名前) があればそれ、無ければ行き先 (dest) の英語
        _stopNames = m.Transit.Where(t => t.Place.Length > 0 || t.Dest.Length > 0)
            .Select(t => (t.Index, Name: t.Place.Length > 0 ? FixShops(t.Place) : t.Dest.StartsWith('@') ? En(t.Dest) : t.Dest))
            .Where(t => !string.IsNullOrEmpty(t.Name)).ToDictionary(t => t.Index, t => t.Name!);
        // 画面に出す乗り場の名前: エレベーターは上の名前。トラム (シャトル) の乗り場は「シャトル乗り場（→ 次の駅）」(利用者の決定。乗り場の番号は出さない)。
        // 次の駅 (nextStop) が無いときは「シャトル乗り場（路線 N）」(N は道筋 (transitPaths) の向きの名前の順の番号)
        var dirs = m.TransitPaths.Select(tp => tp.Dir).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        string? DirOf(string guid) => m.TransitPaths.FirstOrDefault(tp => string.Equals(tp.From, guid, StringComparison.OrdinalIgnoreCase))?.Dir;
        _transitNames = new Dictionary<int, string>(_stopNames);
        foreach (var t in m.Transit.Where(t => t.Kind == "tram"))
        {
            var next = t.NextStop.Length == 0 ? null : t.NextStop.StartsWith('@') ? En(t.NextStop) : t.NextStop;
            var di = DirOf(t.Guid) is { } dr ? dirs.IndexOf(dr) : -1;
            _transitNames[t.Index] = next != null ? $"シャトル乗り場（→ {next}）" : di >= 0 ? $"シャトル乗り場（路線 {di + 1}）" : "シャトル乗り場";
        }
        // 地区の名前: データの名前。鍵 (@…) のままなら英語に引く
        _districtNames = m.Districts.ToDictionary(d => d.Id, d => d.Name.StartsWith('@') ? En(d.Name) ?? d.Name[1..] : d.Name, StringComparer.Ordinal);
    }

    private static readonly System.Text.RegularExpressions.Regex ShopEntityRx = new(@"SCShop_[A-Za-z0-9_\-]+", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // 場所 n が record の場所かその下か
    private static bool Under(ShopTreeNode? n, string record) { for (; n != null; n = n.Parent) if (n.RecordName == record) return true; return false; }

    // 店の名前: ShopTree の店 (shop_id = 場所|socpak|SCShop の CryGUID) のうち CryGUID が同じもの。この場所 (かその下) の店を優先
    // 名前は［詳細］タブと同じ ShopTreeShop.Name (franchise の英語名、無ければ販売データの店名 (ShopTree.ApplyDataNames)、無ければ Entity 名から作った名前)
    // 店名として使うのは、franchise の名前 (HasFranchiseName) か、ゲームの名前・ラベル・販売データの名前 (NameSource が NameFromGame・NameFromLabel・NameFromData) だけ
    // (取り込み側 StationMapExports の店の名前の表と同じ決まり)。アプリが品目から作った名前 (NameFromItems)・種類の言葉 (NameFromKind)・ShopTree に無い店は ""
    // (名前の無い店の扱い。何を出すかは未決定)
    private static string ShopName(ShopTree? tree, string record, StationMapModel.ShopInfo s)
    {
        if (ShopOf(tree, record, s) is { Name.Length: > 0 } best
            && (best.HasFranchiseName || best.NameSource is ShopTreeShop.NameFromGame or ShopTreeShop.NameFromLabel or ShopTreeShop.NameFromData))
            return best.Name;
        return "";
    }

    // リストに出す、ゲームの名前の無い店の行の名前 (仮。何を出すかは未決定)
    private const string ShopNoName = "（名前がデータに無い店）";

    private static ShopTreeShop? ShopOf(ShopTree? tree, string record, StationMapModel.ShopInfo s)
    {
        if (tree == null) return null;
        // shop_id の CryGUID は店ごとに一意ではない (同じ CryGUID が別の Entity 名の店にも付く。例 SCShop_ShipWeapons_UtilStation の CryGUID が
        // ほかの場所の SCShop_lt_a_casaba_small_base_a にも付く) ので、この場所 (かその下) で Entity 名も同じ店を先に選ぶ
        var hits = tree.Shops.Where(x =>
        {
            var parts = x.Id.Split('|');
            return parts.Length >= 3 && string.Equals(parts[2], s.Guid, StringComparison.OrdinalIgnoreCase);
        }).ToList();
        bool SameEntity(ShopTreeShop x) => string.Equals(x.EntityName, s.Entity, StringComparison.OrdinalIgnoreCase);
        return hits.FirstOrDefault(x => Under(x.Location, record) && SameEntity(x)) ?? hits.FirstOrDefault(x => Under(x.Location, record))
            ?? hits.FirstOrDefault(SameEntity) ?? hits.FirstOrDefault();
    }

    // ゲームの文 (作業ディレクトリの english\global.ini と japanese_(japan)\global.ini。［日本語化］の抽出が置くもの。MainWindow の EnPath・JaPath と同じ場所)。
    // 読むのは GlobalIniParser (アプリのほかの所と同じ)。大文字小文字は区別しない。ファイルの更新日時が同じ間は読んだものを使い回す
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

    // キーの文 (MissionService.ResolveLoc と同じ引き方: そのまま / @ 付き / @ 無し、見つからなければ ",P" を付けたキー)。無ければ null
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

    private string FloorDisplay(string fid) => _floorDisplay.TryGetValue(fid, out var n) ? n : fid;

    // ===== リスト =====

    private void BuildLists()
    {
        var m = _model!;
        var floors = m.DrawnFloors();
        // 一覧に出すのはデータが listed にした階だけ。エレベーターで止まるだけの階 (elevatorOnly) は出さない。
        // 並びは地図・［上の階］［下の階］(viewer.js の stackPeers・setDistrictFor) と同じ重ねる並びで、上の階が上 (StackOrder)
        _floorItems = StackOrder(m, floors.Where(f => f.Listed && !f.ElevatorOnly && f.Reachable)).Select(f => new FloorRow(f.Id, FloorDisplay(f.Id), FloorDisplay(f.Id))).ToList();
        _openFloorGroups.Clear();
        // 初めの階: 店のいちばん多い階 (無ければいちばん上)。同じ名前の階のまとまりの中なら、そのまとまりを開いて選ぶ
        _floor = m.Shops.Where(s => floors.Any(f => f.Id == s.Floor)).GroupBy(s => s.Floor).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault()
                 ?? floors.LastOrDefault()?.Id;
        RefreshFloors(RevealFloor(_floor));

        var places = new List<Place>();
        foreach (var s in m.Shops)
            // 「店名（種類）」(地図の名札と同じ)
            places.Add(new Place("shop", s.Index, "", $"店: {(_shopLabels[s.Index].Length > 0 ? _shopLabels[s.Index] : ShopNoName)}{FloorSuffix(s.Floor)}"));
        _hangarLifts = HangarLifts(m);
        _hangarBankOf = HangarBanks(m, _hangarLifts, out var bankNames);
        foreach (var t in m.Transit.Where(t => t.Kind is "elevator" or "tram" || _hangarLifts.Contains(t.Index)))
        {
            if (_hangarLifts.Contains(t.Index))
            {
                // 並びの代表 (HangarBanks) の 1 行だけ
                if (_hangarBankOf[t.Index] == t.Index) places.Add(new Place("transit", t.Index, "", bankNames[t.Index]));
                continue;
            }
            var label = _transitNames.TryGetValue(t.Index, out var n) ? n : "";
            var kind = t.Kind == "tram" ? "シャトル乗り場" : "エレベーター";
            // トラムの乗り場の名前は「シャトル乗り場（→ 次の駅）」なので、種類の言葉を前に重ねない
            places.Add(new Place("transit", t.Index, "", (label.Length == 0 ? kind : label.StartsWith(kind, StringComparison.Ordinal) ? label : $"{kind}: {label}") + FloorSuffix(t.Floor)));
        }
        foreach (var s in m.Sections.Where(s => s.Listed))
            places.Add(new Place("section", s.Index, s.Id, $"区画: {_sectionNames[s.Id]}"));
        // 端末 (まとまりごと。道順は端末の位置からいちばん近い節点。データ側が付けた node)
        // 地図に出せない端末 (階の無いもの・たどれない階のもの) は、一覧・目的地に出さない (選んでも何も強調されないため。2026-11-02、検証の指摘)。数はログに出す
        var hidden = m.TerminalGroups.Count(g => !TerminalOnMap(g));
        if (hidden > 0)
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの地図] 「{_name}」({_record}): 地図に出せない端末 (階が無い・たどれない階) のまとまり {hidden} 件を、端末の一覧と目的地に出しません ({string.Join("・", m.TerminalGroups.Where(g => !TerminalOnMap(g)).GroupBy(g => g.Kind).Select(x => $"{StationTerminalKinds.Label(x.Key)} {x.Count()}"))})");
        foreach (var g in m.TerminalGroups.Where(TerminalOnMap))
            places.Add(new Place("terminal", g.Index, "", $"端末: {TerminalLabel(g)}{TerminalWhere(g)}"));
        _places = places;
        RefreshSections();
        RefreshTerminals();
        RefreshPlaceLists();
        txtRoute.Text = "";
        lstSteps.ItemsSource = null;
    }

    // リストの行の終わりに付ける階 (例「（階: Clinic Floor 05）」。データの階の名前が番号だけのときも、何の番号か分かるよう「階:」を付ける)
    // 区画の階: 今の階に床があればその階、無ければ床のいちばん低い階
    // 床の形の無い区画 (データが listed にした、床の取れない区画) は、箱の中心の底の高さの階 (StationMapModel.FloorOf)
    private string SectionFloor(StationMapModel.SectionInfo s) =>
        _floor != null && s.Floors.ContainsKey(_floor) ? _floor
        : s.Floors.Keys.Select(k => _model!.Floors.FirstOrDefault(f => f.Id == k)).Where(f => f != null).OrderBy(f => f!.Z).FirstOrDefault()?.Id
          ?? _model!.FloorOf((s.Center.X, s.Center.Y, s.Center.Z - s.Half.Z + 0.5))?.Id ?? "";

    // 階の一覧の並び (上の階が上): ビューアの重ねる並びと同じまとまりと順番。
    //   まとまり: 地区 (districts の groups) に入る階はその地区、無ければデータの重ねる並び (stackGroup)、無ければ床のまとまり (group)。
    //   まとまりの中の順: 地区は、地区の全部の階に stackLevel があればその順、無ければ高さの順 (setDistrictFor)。stackGroup は stackLevel、group は level (ビューアの stack0)。同じなら高さ。
    //   まとまりの無い階は高さの順 (今までどおり)。まとまりどうしは、まとまりのいちばん高い階の高さの順 (高いほうが上)
    private static List<StationFloor> StackOrder(StationMapModel m, IEnumerable<StationFloor> listed)
    {
        var distOfGroup = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var d in m.Districts) foreach (var g in d.Groups) distOfGroup.TryAdd(g, d.Id);
        var distBySl = m.Districts.ToDictionary(d => d.Id, d => m.Floors.Where(f => distOfGroup.TryGetValue(f.Group, out var x) && x == d.Id).All(f => m.FloorStack.ContainsKey(f.Id)), StringComparer.Ordinal);
        (string Key, double Level) KeyOf(StationFloor f)
        {
            if (f.Group.Length > 0 && distOfGroup.TryGetValue(f.Group, out var did))
                return ("d:" + did, distBySl[did] ? m.FloorStack[f.Id].Level : f.Z);
            if (m.FloorStack.TryGetValue(f.Id, out var sk)) return ("s:" + sk.Group, sk.Level);
            if (f.Group.Length > 0) return ("g:" + f.Group, f.Level);
            return ("", f.Z);
        }
        var rows = listed.Select(f => (F: f, K: KeyOf(f))).ToList();
        var top = rows.Where(r => r.K.Key.Length > 0).GroupBy(r => r.K.Key).ToDictionary(g => g.Key, g => g.Max(r => r.F.Z), StringComparer.Ordinal);
        return rows.OrderByDescending(r => r.K.Key.Length > 0 ? top[r.K.Key] : r.F.Z)
            .ThenBy(r => r.K.Key, StringComparer.Ordinal)
            .ThenByDescending(r => r.K.Level).ThenByDescending(r => r.F.Z)
            .Select(r => r.F).ToList();
    }

    // 階の一覧の行を作り直す。同じ表示の名前の階 (数字だけ違う名前も。FloorGroupKey) が 2 つ以上あれば (括弧で見分けた階は括弧の中が数字だけ違うときだけ入る)、
    // 「▼ 名前（N 件）」の 1 行にまとめる (名前は FloorGroupTitle) (数の言葉は「階」にしない。「（2 階）」が 2 階目と読めるため) (利用者の指示。ステーションの別ウィンドウの場所の一覧 (StationMapWindow.GroupRows) と同じ見た目と動き:
    // 閉じているときは ▼ (押すと開く)、開いているときは ▲ (押すと閉じる)、中の行は一段下げる)。
    // まとまりの行は、まとまりの中のいちばん上の階 (重ねる並びで最初の階) の所に出す。ほかの階の並びは変えない。
    // 開いた中の行は重ねる並びの順で、名前にその階の高さ (StationFloor.Z = 板の上面の中央値。一覧の並び StackOrder・区画の階 SectionFloor と同じ値) を
    // 「名前（-14.0 m）」の形 (小数 1 桁、符号付き。0 は「0.0 m」) で添える (2026-11-01、利用者の決定「C」)。
    // 同じ高さになる階 (例: RR_P6_L5 の Maintenance Shaft 02 の 2 つは、どちらも -98.6 m) は、そのまま同じ高さを出す (番号などは足さない)。
    // 選んでいる階 (_floor) の行を選ぶ (閉じたまとまりの中なら、何も選ばない)。scroll: 選んだ行が見えるようにする
    private void RefreshFloors(bool scroll = false)
    {
        var groups = _floorItems.GroupBy(r => FloorGroupKey(r.Name), StringComparer.Ordinal).Where(g => g.Count() > 1).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var rows = new List<FloorRow>();
        var done = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in _floorItems)
        {
            var key = FloorGroupKey(r.Name);
            if (!groups.TryGetValue(key, out var members)) { rows.Add(r with { Display = r.Name + StairPartners(r.Id) }); continue; }
            if (!done.Add(key)) continue;
            var open = _openFloorGroups.Contains(key);
            rows.Add(new FloorRow("", r.Name, $"{(open ? "▲" : "▼")} {FloorGroupTitle(members)}（{members.Count} 件）", key, IsGroup: true));
            if (open) rows.AddRange(members.Select(x => x with { Display = $"　　{x.Name}（{FloorHeight(x.Id)}）{StairPartners(x.Id)}", Group = key }));
        }
        _suppress = true;
        try
        {
            lstFloors.ItemsSource = rows;
            lstFloors.SelectedItem = rows.FirstOrDefault(r => !r.IsGroup && r.Id == _floor);
            if (scroll && lstFloors.SelectedItem != null) lstFloors.ScrollIntoView(lstFloors.SelectedItem);
        }
        finally { _suppress = false; }
    }

    // 階段でつながる階の名前 (2026-11-01、利用者の決まり「階段でつながっているフロアには双方にサブの名前として相手の名前を付けるルールにする」)。
    // 相手: 保存した地図の階段 (JSON の stairs の from・to。層の決まりで分かれた階どうしの階段だけが入る。同じ層の中の数段の階段は床に入るので入らない) で、
    //   この階とつながる、階の一覧に出る階 (_floorItems)。一覧に出ない階 (listed でない「付近」の階など) は相手に入れず、階段の道の途中とみなしてたどり、
    //   最初に着いた一覧に出る階で止める (2026-11-01、親の決定「B」: 一覧に出ない階をはさんでつながる階も、階段でつながっている。一度たどった階はたどらない)。
    // 文字: 「（階段で: A・B・C）」(一覧の並びの順。同じ文字は 1 回。4 つ以上なら 3 つと「ほか N」)。相手の名前が一覧に 2 つ以上ある名前 (▼ のまとまりになる名前) なら、
    //   どの階か分かるよう高さ (FloorHeight) を添える (例「Aparelli +13.0 m」)。相手が無ければ ""。
    // 地図の階の名札 (viewer.js の stairPartners) も同じ決まり
    private const int StairPartnerMax = 3;
    private string StairPartners(string fid)
    {
        var m = _model;
        if (m == null) return "";
        var order = _floorItems.Select((r, i) => (r.Id, i)).ToDictionary(t => t.Id, t => t.i, StringComparer.Ordinal);
        var next = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var st in m.Stairs.Where(st => st.From != st.To))
        {
            (next.TryGetValue(st.From, out var a) ? a : next[st.From] = new()).Add(st.To);
            (next.TryGetValue(st.To, out var b) ? b : next[st.To] = new()).Add(st.From);
        }
        var found = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal) { fid };
        var queue = new Queue<string>();
        queue.Enqueue(fid);
        while (queue.Count > 0)
            foreach (var g in next.TryGetValue(queue.Dequeue(), out var ns) ? ns : new List<string>())
            {
                if (!seen.Add(g)) continue;
                if (order.ContainsKey(g)) found.Add(g); else queue.Enqueue(g);
            }
        var partners = found.OrderBy(g => order[g]).ToList();
        if (partners.Count == 0) return "";
        var dupNames = _floorItems.GroupBy(r => r.Name, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var names = partners.Select(g => FloorDisplay(g) is var n && dupNames.Contains(n) ? $"{n} {FloorHeight(g)}" : FloorDisplay(g))
            .Distinct(StringComparer.Ordinal).ToList();
        var shown = string.Join("・", names.Take(StairPartnerMax));
        return names.Count > StairPartnerMax ? $"（階段で: {shown} ほか {names.Count - StairPartnerMax}）" : $"（階段で: {shown}）";
    }

    // 階の高さの文字 (RefreshFloors のまとまりの中の行): StationFloor.Z を小数 1 桁、符号付きで「-14.0 m」「+4.3 m」「0.0 m」。階が引けなければ ""
    private string FloorHeight(string fid) =>
        _model?.Floors.FirstOrDefault(f => f.Id == fid) is { } f
            ? f.Z.ToString("+0.0;-0.0;0.0", System.Globalization.CultureInfo.InvariantCulture) + " m" : "";

    // 階が同じ名前の階のまとまりの中なら、そのまとまりを開く。新しく開いたら true
    private bool RevealFloor(string? fid)
    {
        if (fid == null || _floorItems.FirstOrDefault(r => r.Id == fid) is not { } row) return false;
        var key = FloorGroupKey(row.Name);
        if (_floorItems.Count(r => string.Equals(FloorGroupKey(r.Name), key, StringComparison.Ordinal)) < 2) return false;
        return _openFloorGroups.Add(key);
    }

    // 階のまとまりの鍵: 名前の数字を # にしたもの (数字だけ違う名前 (Clinic Floor 01・Clinic Floor 02 など) も同じまとまりにする。2026-11-02、利用者の指示)
    private static readonly System.Text.RegularExpressions.Regex DigitsRx = new(@"\d+", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static string FloorGroupKey(string name) => DigitsRx.Replace(name, "#");

    // まとまりの見出しの名前: 名前がみな同じならその名前。数字の所が 1 か所なら、その数字を「小さい方〜大きい方」にしたもの (「Clinic Floor 01〜10」)。
    // 数字の所が 2 か所以上なら、番号の順で最初と最後の名前を「〜」でつないだもの (「Floor 04（Floor 01）〜Floor 07（Floor 02）」)
    private static string FloorGroupTitle(List<FloorRow> members)
    {
        var names = members.Select(m => m.Name).Distinct(StringComparer.Ordinal).ToList();
        if (names.Count == 1) return names[0];
        long Num(string s) => long.TryParse(s, out var v) ? v : long.MaxValue;
        var nums = names.Select(n => DigitsRx.Matches(n).Select(x => x.Value).ToList()).ToList();
        if (nums.All(x => x.Count == 1))
        {
            var lo = nums.Select(x => x[0]).MinBy(Num)!;
            var hi = nums.Select(x => x[0]).MaxBy(Num)!;
            var m0 = DigitsRx.Match(names[0]);
            return names[0][..m0.Index] + $"{lo}〜{hi}" + names[0][(m0.Index + m0.Length)..];
        }
        var sorted = names.Select((n, i) => (n, k: nums[i].Select(Num).ToList()))
            .OrderBy(t => t.k, Comparer<List<long>>.Create((a, b) => { for (var i = 0; i < Math.Min(a.Count, b.Count); i++) { var c = a[i].CompareTo(b[i]); if (c != 0) return c; } return a.Count.CompareTo(b.Count); }))
            .Select(t => t.n).ToList();
        return $"{sorted[0]}〜{sorted[^1]}";
    }

    private string FloorSuffix(string fid) => fid.Length > 0 ? $"（階: {FloorDisplay(fid)}）" : "";

    // ハンガーのエレベーター (2026-11-02、利用者の指示「ハンガーや tram の駅から道案内を出したい」): 乗らない (elevator-noride) エレベーターのうち、
    // 同じ路線 (Line) のエレベーターの乗り場が全部同じ階にあり、その路線の行き先 (destination) が地図の中に 1 つ以下のもの
    // (路線のもう一方の止まる所 (ハンガー) が地図の外。名前では決めない)。取り込み済みの 91 か所では Area18 12・Lorville 10・New Babbage 7・GrimHEX 4・
    // ASD Delving 2 が当たり、ほかの乗り物は入らない。道順の節点 (Node) はある (StationRouteGraph の gateway の節点)
    private const string HangarLift = "ハンガーのエレベーター";
    private HashSet<int> _hangarLifts = new();
    // ハンガーのエレベーター → その並びの代表 (一覧の 1 行。HangarBanks)
    private Dictionary<int, int> _hangarBankOf = new();
    // 同じ並びとみなす、ハンガーのエレベーターどうしの xy の距離 (m)。取り込み済みの都市では、並びのエレベーターは 30 m 四方ほどに固まっている
    private const double HangarBankGap = 40;

    // ハンガーのエレベーターを並びにまとめる: 同じ階で、xy が HangarBankGap 以内でつながるもの。並びの代表は、並びの真ん中 (xy の平均) にいちばん近いもの。
    // 一覧の名前は「ハンガーのエレベーター（階: …）」。同じ階に並びが 2 つ以上あれば、名前の後に番号 (並びの真ん中の x・y の順) を付ける。
    // 返すのは エレベーター → 代表。names は 代表 → 一覧の名前
    private Dictionary<int, int> HangarBanks(StationMapModel m, HashSet<int> lifts, out Dictionary<int, string> names)
    {
        var res = new Dictionary<int, int>();
        names = new Dictionary<int, string>();
        var banks = new List<(string Floor, List<StationMapModel.TransitInfo> Lifts)>();
        foreach (var byFloor in m.Transit.Where(t => lifts.Contains(t.Index)).GroupBy(t => t.Floor, StringComparer.Ordinal))
        {
            var left = byFloor.ToList();
            while (left.Count > 0)
            {
                var bank = new List<StationMapModel.TransitInfo> { left[0] };
                left.RemoveAt(0);
                for (int i = 0; i < bank.Count; i++)
                {
                    var a = bank[i];
                    var near = left.Where(b => Math.Sqrt((a.Pos.X - b.Pos.X) * (a.Pos.X - b.Pos.X) + (a.Pos.Y - b.Pos.Y) * (a.Pos.Y - b.Pos.Y)) <= HangarBankGap).ToList();
                    foreach (var b in near) { bank.Add(b); left.Remove(b); }
                }
                banks.Add((byFloor.Key, bank));
            }
        }
        foreach (var byFloor in banks.GroupBy(b => b.Floor, StringComparer.Ordinal))
        {
            var list = byFloor.Select(b => (b.Lifts, X: b.Lifts.Average(t => t.Pos.X), Y: b.Lifts.Average(t => t.Pos.Y))).OrderBy(b => b.X).ThenBy(b => b.Y).ToList();
            for (int k = 0; k < list.Count; k++)
            {
                var (bl, cx, cy) = list[k];
                var rep = bl.MinBy(t => (t.Pos.X - cx) * (t.Pos.X - cx) + (t.Pos.Y - cy) * (t.Pos.Y - cy))!;
                foreach (var t in bl) res[t.Index] = rep.Index;
                names[rep.Index] = HangarLift + (list.Count > 1 ? $" {k + 1}" : "") + FloorSuffix(byFloor.Key);
            }
        }
        return res;
    }
    private static HashSet<int> HangarLifts(StationMapModel m)
    {
        var res = new HashSet<int>();
        foreach (var t in m.Transit.Where(t => t.Kind == "elevator-noride" && t.Line.Length > 0))
        {
            var floors = m.Transit.Where(x => x.Line == t.Line && x.Kind.StartsWith("elevator", StringComparison.Ordinal)).Select(x => x.Floor).Distinct(StringComparer.Ordinal).Count();
            var dests = m.Transit.Count(x => x.Kind == "destination" && x.Line == t.Line);
            if (floors == 1 && dests <= 1) res.Add(t.Index);
        }
        return res;
    }

    private void RefreshSections()
    {
        if (_model == null) return;
        var q = txtSectionFilter.Text.Trim();
        // データが listed にした区画 (全部の階から)。選ぶとその区画の階に切り替える (LstSections_SelectionChanged)
        var list = _model.Sections.Where(s => s.Listed)
            .Select(s => new SectionRow(s.Id, _sectionNames[s.Id] + FloorSuffix(SectionFloor(s))))
            .Where(r => q.Length == 0 || r.Display.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Display, StringComparer.OrdinalIgnoreCase).ToList();
        var sel = (lstSections.SelectedItem as SectionRow)?.Id;
        _suppress = true;
        try { lstSections.ItemsSource = list; lstSections.SelectedItem = list.FirstOrDefault(r => r.Id == sel); }
        finally { _suppress = false; }
    }

    // ===== 端末 =====

    // 地図に出せる端末のまとまり (階があり、たどれる階のもの。StationMapModel.ViewerMap が地図に送るものと同じ)
    private bool TerminalOnMap(StationMapModel.TerminalGroup g) => g.Floor.Length > 0 && _model?.UnreachableFloors().Contains(g.Floor) != true;

    // 端末のまとまりの名前: 種類の言葉 (StationTerminalKinds。ゲームの訳) と、まとめた数 (2 つ以上なら「×N」)
    private static string TerminalLabel(StationMapModel.TerminalGroup g) =>
        StationTerminalKinds.Label(g.Kind) + (g.Members.Count > 1 ? $" ×{g.Members.Count}" : "");

    // 端末のまとまりの場所「（階: …・区画: …）」(階の無い端末は「（階: なし）」。区画は名前のあるときだけ)
    private string TerminalWhere(StationMapModel.TerminalGroup g)
    {
        var floor = g.Floor.Length > 0 ? FloorDisplay(g.Floor) : "なし";
        var sec = g.Section.Length > 0 && _sectionNames.TryGetValue(g.Section, out var sn) && sn.Length > 0 ? $"・区画: {sn}" : "";
        return $"（階: {floor}{sec}）";
    }

    // 端末の一覧 (種類の順 (StationTerminalKinds)・階・位置の順。絞り込みは行の文字)
    private void RefreshTerminals()
    {
        if (_model == null) return;
        var q = txtTerminalFilter.Text.Trim();
        var list = _model.TerminalGroups.Where(TerminalOnMap).Select(g => new TerminalRow(g.Index, TerminalLabel(g) + TerminalWhere(g)))
            .Where(r => q.Length == 0 || r.Display.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        var sel = (lstTerminals.SelectedItem as TerminalRow)?.Group;
        _suppress = true;
        try { lstTerminals.ItemsSource = list; lstTerminals.SelectedItem = list.FirstOrDefault(r => r.Group == sel); }
        finally { _suppress = false; }
    }

    private void TxtTerminalFilter_TextChanged(object sender, TextChangedEventArgs e) => RefreshTerminals();

    // 端末を選んだ: その階に移り (階の一覧も合わせる)、端末の位置に寄せて強調する。販売品の枠は消す
    private void LstTerminals_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || lstTerminals.SelectedItem is not TerminalRow row || _model is not { } m || row.Group < 0 || row.Group >= m.TerminalGroups.Count) return;
        var g = m.TerminalGroups[row.Group];
        SendSales(null);
        if (g.Floor.Length > 0)
        {
            SelectFloorInList(g.Floor);
            Send(new { type = "floor", id = g.Floor });
            Send(new { type = "hlterm", index = g.Index });
            Send(new { type = "focus", pos = new[] { g.Pos.X, g.Pos.Y, g.Pos.Z }, floor = g.Floor, size = 40 });
        }
        else Send(new { type = "hlterm", index = g.Index });
    }

    // 端末のアイコン (ゲームのアイコン。初めて使うときに利用者の Data.p4k から取り出す。GameIcons)。取れたらビューアへ送る
    private async Task SendIconsAsync()
    {
        try
        {
            var icons = await StationTerminalKinds.IconsAsync();
            if (icons.Count > 0) Send(new { type = "icons", icons });
        }
        catch (Exception ex) { App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの地図] 端末のアイコンを送れませんでした: {ex.Message}"); }
    }

    // ===== 店の販売品の枠 =====
    // (2026-11-02、利用者の指示「区画でお店を選んでそのエリアがハイライトされたとき、右側に四角で囲んだ販売品リスト(価格も)を出せれば素晴らしい。他をクリックしたら消してほしい」)
    // 区画 (sectionId) の店 (地図の店のうち section が同じもの) ごとに、［詳細］のお店の【売っている物】と同じデータ (ShopTree の店 ShopTreeShop の Items・Commodities) を
    // 同じ形でビューアの枠に出す: 品目は「品名・種類・価格 (購入とレンタルが両方あれば 購入・レンタル)」、コモディティは「品名・買値・売値」。値段の無い所は「-」(［詳細］と同じ)。
    // 同じ品名・種類・値段の行は 1 行にまとめる (［詳細］と同じ)。並びは品名の順。販売データの無い店は「販売データが紐づいていません。」(［詳細］と同じ文)。
    // 店の名前は地図の名札と同じ「店名（種類）」。区画に店が無ければ枠を消す (sectionId が null でも消す)
    private void SendSales(string? sectionId)
    {
        var m = _model;
        if (m == null || sectionId == null || BuildSales(m, _tree, _shopLabels, _sectionNames, sectionId) is not { } msg) { Send(new { type = "sales" }); return; }
        Send(msg);
    }

    // 販売品の枠の知らせ ({type:'sales', title, shops})。区画に店が無ければ null (確かめの道具からも呼ぶので static)
    internal static object? BuildSales(StationMapModel m, ShopTree? tree, IReadOnlyDictionary<int, string> shopLabels, IReadOnlyDictionary<string, string> sectionNames, string sectionId)
    {
        var _tree = tree; var _shopLabels = shopLabels; var _sectionNames = sectionNames;
        var shops = m.Shops.Where(s => s.Section == sectionId).ToList();
        if (shops.Count == 0) return null;
        var list = new List<object>();
        // 同じ店 (ShopTree の店が同じ) は 1 つだけ
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in shops)
        {
            var shop = ShopOf(_tree, m.Record, s);
            if (shop != null && !seen.Add(shop.Record)) continue;
            var name = _shopLabels.TryGetValue(s.Index, out var l) && l.Length > 0 ? l : ShopNoName;
            if (shop == null || shop.Items.Count == 0 && shop.Commodities.Count == 0)
            {
                list.Add(new { name, note = "販売データが紐づいていません。" });
                continue;
            }
            var tables = new List<object>();
            if (shop.Items.Count > 0)
            {
                var items = shop.Items.Values.ToList();
                var hasBuy = items.Any(i => i.Buy.HasValue);
                var hasRent = items.Any(i => i.Rents.Count > 0);
                var cols = new List<string> { "品名", "種類" };
                if (hasBuy) cols.Add(hasRent ? "購入" : "価格");
                if (hasRent) cols.Add("レンタル");
                var rows = items.Select(i => (i.Name, i.Type,
                        Buy: i.Buy is { } b ? $"{b:N0} aUEC" : "-",
                        Rent: i.Rents.Count > 0
                            ? string.Join(" / ", i.Rents.Distinct().OrderBy(r => r.Days ?? int.MaxValue).ThenBy(r => r.Price).Select(r => $"{(r.Days is { } d ? $"{d}日 " : "")}{r.Price:N0} aUEC"))
                            : "-"))
                    .Distinct().OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Type, StringComparer.OrdinalIgnoreCase)
                    .Select(r =>
                    {
                        var row = new List<string> { r.Name, r.Type };
                        if (hasBuy) row.Add(r.Buy);
                        if (hasRent) row.Add(r.Rent);
                        return row;
                    }).ToList();
                tables.Add(new { cols, rows, left = 2 });
            }
            if (shop.Commodities.Count > 0)
            {
                static string P(double v) => v > 0 ? $"{v:N0} aUEC" : "-";
                var rows = shop.Commodities.Distinct().OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(c => new List<string> { c.Name, P(c.Buy), P(c.Sell) }).ToList();
                tables.Add(new { cols = new List<string> { "品名", "買値", "売値" }, rows, left = 1 });
            }
            list.Add(new { name, tables });
        }
        var title = _sectionNames.TryGetValue(sectionId, out var sn) && sn.Length > 0 ? $"販売品: {sn}" : "販売品";
        return new { type = "sales", title, shops = list };
    }

    private void RefreshPlaceLists()
    {
        var head = new List<Place> { new("current", -1, StartCurrentId, "現在位置（/showlocation）") };
        if (_pin is { } pin) head.Add(new Place("pin", -1, StartPinId, $"ピン（{FloorDisplay(pin.Floor)}）"));
        Fill(lstStart, head.Concat(_places), txtStartFilter.Text, _start);
        Fill(lstDest, _places, txtDestFilter.Text, _dest);
    }

    private void Fill(ListBox box, IEnumerable<Place> items, string filter, Place? selected)
    {
        var q = filter.Trim();
        var list = items.Where(p => q.Length == 0 || p.Display.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        _suppress = true;
        try { box.ItemsSource = list; box.SelectedItem = selected == null ? null : list.FirstOrDefault(p => Same(p, selected)); }
        finally { _suppress = false; }
        if (box.SelectedItem != null) box.ScrollIntoView(box.SelectedItem);
    }

    private static bool Same(Place a, Place b) => a.Kind == b.Kind && a.Index == b.Index && a.Id == b.Id;

    private void TxtSectionFilter_TextChanged(object sender, TextChangedEventArgs e) => RefreshSections();
    private void TxtStartFilter_TextChanged(object sender, TextChangedEventArgs e) => RefreshPlaceLists();
    private void TxtDestFilter_TextChanged(object sender, TextChangedEventArgs e) => RefreshPlaceLists();

    private void LstFloors_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || lstFloors.SelectedItem is not FloorRow row) return;
        if (row.IsGroup)
        {
            // まとまりの行: 押すと開く・閉じる。選んでいる階はそのまま (作り直した一覧で選び直す)
            if (!_openFloorGroups.Remove(row.Group!)) _openFloorGroups.Add(row.Group!);
            RefreshFloors();
            return;
        }
        _floor = row.Id;
        RefreshSections();
        SendSales(null);
        Send(new { type = "floor", id = row.Id });
    }

    private void LstSections_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || lstSections.SelectedItem is not SectionRow row || _model?.Sections.FirstOrDefault(s => s.Id == row.Id) is not { } sec) return;
        var fid = SectionFloor(sec);
        if (fid.Length > 0) SelectFloorInList(fid);
        Send(new { type = "section", id = row.Id, floor = fid });
        // 店のある区画は、地図の右に販売品を出す (店の無い区画は消す)
        SendSales(row.Id);
        // 床の形の無い区画は、箱の中心に寄せる (大きさは箱に合わせる)
        if (sec.Floors.Count == 0)
            Send(new { type = "focus", pos = new[] { sec.Center.X, sec.Center.Y, sec.Center.Z }, floor = fid, size = Math.Max(40, 2 * Math.Max(sec.Half.X, sec.Half.Y)) });
    }

    private void LstStart_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || lstStart.SelectedItem is not Place p) return;
        SendSales(null);
        _start = p;
        SendEnds();
        _ = RouteAsync();
    }

    private void LstDest_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || lstDest.SelectedItem is not Place p) return;
        SendSales(null);
        _dest = p;
        SendEnds();
        _ = RouteAsync();
    }

    private void ChkTop_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressTop) return;
        Send(new { type = "mode", top = chkTop.IsChecked == true });
    }

    // 出発地を現在位置にした、ボタンで取った座標の時刻 (MapPanel.LastButtonCaptureUtc)
    private double _startFromButtonUtc;

    // ［現在位置］のボタン (どの画面のものでも) で取った座標が今の現在位置で、まだ出発地にしていなければ、出発地を現在位置にして道順を出し直す。
    // 現在位置がこのステーションの中 (区画の中・区画の外でも範囲の中) なら、地図を現在位置の階 (都市なら現在位置の地区のその階) に移し、印に寄せる
    // (ビューアの gotoCurrent。道順を出したときの寄せ方にも印を入れる)。行ったら true
    private bool ApplyButtonCapture()
    {
        var utc = MapPanel.LastButtonCaptureUtc;
        if (_model == null || utc <= 0 || utc != App.Config.MapCurrent.Utc || utc == _startFromButtonUtc) return false;
        _startFromButtonUtc = utc;
        _start = new Place("current", -1, StartCurrentId, "");
        RefreshPlaceLists();
        SendEnds();
        var inside = _cur is { State: not ("Outside" or "Hangar" or "HangarGuess") };
        if (inside) Send(new { type = "gotoCurrent" });
        else HangarFocus();
        _ = RouteAsync(withCurrent: inside);
        return true;
    }

    // ［現在位置］: マップタブの［現在位置］と同じ処理 (StationMapWeb.CaptureHook) で取り、出発地にする
    private async void BtnCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (StationMapWeb.CaptureHook is not { } capture)
        {
            txtCurrent.Text = "［現在位置］の取得を使えません (マップタブの［現在位置］で取った位置は赤い点で出します)";
            return;
        }
        btnCurrent.IsEnabled = false;
        (bool Ok, string Status) res;
        // アプリのログに出す、押した画面 (［詳細］の中か、別ウィンドウか)
        var from = Window.GetWindow(this) is StationMapWindow ? "ステーションの地図（別ウィンドウ）" : "ステーションの地図（［詳細］）";
        try { res = await capture(from); }
        catch (Exception ex)
        {
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの地図] 現在位置を取れませんでした: {ex}");
            res = (false, $"現在位置を取れませんでした: {ex.Message}");
        }
        finally { btnCurrent.IsEnabled = true; }
        // ゲームの窓を前に出していたので、この欄の窓を前に戻す (マップタブの処理が戻すのはアプリの主の窓)
        if (Window.GetWindow(this) is { } w)
        {
            var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            if (h != IntPtr.Zero) ShowLocationSender.BringToFront(h);
            w.Activate();
        }
        if (!res.Ok)
        {
            if (TryHangarFromGameLog() is { } note)
            {
                txtCurrent.Text = note;
                _start = new Place("current", -1, StartCurrentId, "");
                RefreshPlaceLists();
                SendCurrent();
                SendEnds();
                HangarFocus();
                _ = RouteAsync();
                return;
            }
            txtCurrent.Text = res.Status.Length > 0 ? res.Status : "現在位置を取れませんでした";
            return;
        }
        UpdateCurrent();
        ApplyButtonCapture();
    }

    // ===== ビューア =====

    // 地図を送ったのに、ビューアから読み終わった知らせ (loaded) が LoadWatchSeconds 秒たっても来ないとき・ビューアが失敗を知らせたとき (2026-11-02、
    // 利用者の指摘「HUR-L5 マップがでない」「ログにエラーの情報も出てない」): 地図の欄に知らせを出し、アプリのログに書く (空白のままにしない)。
    // 値: 送る地図は大きいもので数 MB (Area18 2.4 MB) で、ビューアは数秒以内に描き終える (Chrome headless で確かめた描画は数秒) ので、余裕を見て 20 秒
    private const int LoadWatchSeconds = 20;
    private readonly DispatcherTimer _loadWatch = new() { Interval = TimeSpan.FromSeconds(LoadWatchSeconds) };
    private bool _loadWatchHooked;
    private const string ViewerFailText = "地図を表示できませんでした（アプリのログを見てください）";

    private void StartLoadWatch()
    {
        if (!_loadWatchHooked)
        {
            _loadWatchHooked = true;
            _loadWatch.Tick += (_, _) =>
            {
                _loadWatch.Stop();
                if (_model == null || _disposed) return;
                // 地図の欄が見えていない間 (［マップ］タブで星系図を出している・別のタブ) は数えない (見えるまで待つ。2026-11-02、検証の指摘)
                if (!IsVisible) { _loadWatch.Start(); return; }
                App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの地図] ビューアの失敗: 「{_name}」({_record}) の地図を送ってから {LoadWatchSeconds} 秒たっても、ビューアから読み終わった知らせが来ません");
                ShowViewerFail();
            };
        }
        _loadWatch.Stop();
        _loadWatch.Start();
    }

    private void OnViewerError(JsonElement msg)
    {
        // 地図を読むところ (load) の失敗だけ、地図の欄に知らせを出す (ほかの操作の失敗はログだけ。ビューアの下にも短く出る)
        var where = msg.TryGetProperty("where", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() : "";
        if (where != "load") return;
        _loadWatch.Stop();
        ShowViewerFail();
    }

    // 地図の欄の上に知らせを出す (地図は消さない。合成で描く WebView2 では知らせが地図の上に出る。HWND の WebView2 ではビューアの中の知らせ (viewer.js の fail) が出る)
    private void ShowViewerFail()
    {
        txtOverlay.Text = ViewerFailText;
        txtOverlay.Visibility = Visibility.Visible;
    }

    private void Send(object msg)
    {
        if (!_viewerReady || _model == null) return;
        StationMapWeb.Post(Web, JsonSerializer.Serialize(msg));
    }

    private void SendLoad()
    {
        var m = _model;
        if (m == null) return;
        if (_viewerMap == null) return;
        var sb = new StringBuilder(_viewerMap.Length + 256);
        sb.Append("{\"type\":\"load\",\"map\":").Append(_viewerMap)
          .Append(",\"floor\":").Append(JsonSerializer.Serialize(_floor))
          .Append(",\"top\":").Append(chkTop.IsChecked == true ? "true" : "false").Append('}');
        StationMapWeb.Post(Web, sb.ToString());
        StartLoadWatch();
        SendCurrent();
        if (_pin is { } pin) Send(new { type = "pin", pos = new[] { pin.Pos.X, pin.Pos.Y, pin.Pos.Z }, floor = pin.Floor });
        SendEnds();
        _ = SendIconsAsync();
        if (_pendingFocus is { } focus) { _pendingFocus = null; Send(focus); }
    }

    private void SendCurrent()
    {
        // ハンガーの中 (地図に床が無い) は点を出さず、ハンガーロビー (艦隊マネージャーの端末) を強調する (HangarFocus)
        if (_cur is { State: "Hangar" or "HangarGuess" })
        {
            Send(new { type = "current", pos = (double[]?)null });
            if (HangarLobby() is { } lob) Send(new { type = "hlterm", index = lob.Index });
            return;
        }
        if (_cur is { } c && c.State != "Outside")
            Send(new { type = "current", pos = new[] { c.Pos.X, c.Pos.Y, c.Pos.Z }, floor = c.Floor, label = "現在位置" });
        else Send(new { type = "current", pos = (double[]?)null });
    }

    private void SendEnds()
    {
        object? Ref(Place? p) => p == null ? null : p.Kind switch
        {
            "shop" or "transit" or "terminal" => new { kind = p.Kind, index = p.Index },
            "section" => new { kind = "section", id = p.Id },
            _ => null,
        };
        Send(new { type = "ends", start = Ref(_start), dest = Ref(_dest) });
    }

    private void OnViewerMessage(JsonElement msg)
    {
        var type = msg.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "ready":
                _viewerReady = true;
                SendLoad();
                break;
            case "loaded":
                _loadWatch.Stop();
                break;
            case "hlterm-cleared":
                // 地図を押して端末の強調が消えた: 左の「端末」の一覧の選択も外す (同じ行を選び直せるように)
                _suppress = true;
                try { lstTerminals.SelectedItem = null; }
                finally { _suppress = false; }
                break;
            case "floor":
                if (msg.TryGetProperty("id", out var fid) && fid.GetString() is { } f) SelectFloorInList(f);
                break;
            case "zoomout":
                ZoomedOut?.Invoke();
                break;
            case "mode":
                // ビューアの［全体図］で斜めの図に戻したとき: 「この階だけ（真上から）」の欄を合わせる (欄の変更の知らせは送り返さない)
                if (msg.TryGetProperty("top", out var tp))
                {
                    _suppressTop = true;
                    try { chkTop.IsChecked = tp.ValueKind == JsonValueKind.True; }
                    finally { _suppressTop = false; }
                }
                break;
            case "section":
                if (msg.TryGetProperty("id", out var sid) && sid.GetString() is { } id)
                {
                    if (msg.TryGetProperty("floor", out var sf) && sf.GetString() is { } sfl) SelectFloorInList(sfl);
                    if (txtSectionFilter.Text.Length > 0) { _suppress = true; txtSectionFilter.Text = ""; _suppress = false; RefreshSections(); }
                    _suppress = true;
                    try
                    {
                        lstSections.SelectedItem = (lstSections.ItemsSource as List<SectionRow>)?.FirstOrDefault(r => r.Id == id);
                        if (lstSections.SelectedItem != null) lstSections.ScrollIntoView(lstSections.SelectedItem);
                    }
                    finally { _suppress = false; }
                    // 地図で区画を押した: 店のある区画なら販売品を出す
                    SendSales(id);
                }
                break;
            case "pick":
                {
                    var kind = msg.TryGetProperty("kind", out var k) ? k.GetString() : null;
                    var index = msg.TryGetProperty("index", out var ix) && ix.ValueKind == JsonValueKind.Number ? ix.GetInt32() : -1;
                    // ハンガーのエレベーターは、並びの代表の行 (HangarBanks)
                    if (kind == "transit" && _hangarBankOf.TryGetValue(index, out var bankRep)) index = bankRep;
                    var p = _places.FirstOrDefault(x => x.Kind == kind && x.Index == index);
                    if (p == null) break;
                    // 店の印を押した: その店の区画の販売品を出す
                    if (kind == "shop" && _model is { } pm && index >= 0 && index < pm.Shops.Count) SendSales(pm.Shops[index].Section);
                    _dest = p;
                    txtDestFilter.Text = "";
                    RefreshPlaceLists();
                    SendEnds();
                    _ = RouteAsync();
                    break;
                }
            case "pin":
                {
                    if (!msg.TryGetProperty("pos", out var pos) || pos.ValueKind != JsonValueKind.Array || pos.GetArrayLength() < 3) break;
                    var floor = msg.TryGetProperty("floor", out var pf) ? pf.GetString() ?? "" : "";
                    _pin = ((pos[0].GetDouble(), pos[1].GetDouble(), pos[2].GetDouble()), floor);
                    // 立てたピンは出発地にする
                    _start = new Place("pin", -1, StartPinId, "");
                    txtStartFilter.Text = "";
                    RefreshPlaceLists();
                    SendEnds();
                    _ = RouteAsync();
                    break;
                }
        }
    }

    // ［詳細］の店の［マップに表示］(MapPanel.ShowShopInMapTab): 出している地図の中のその店を目的地にし (地図の上で店を押したとき (ビューアの pick) と同じ)、
    // 店の階を開いて店に寄せる (区画の一覧で区画を選んだときと同じく、階の一覧を合わせてビューアに floor と focus を送る)。
    // 店は名札の名前と同じ結び方 (ShopOf: CryGUID と Entity 名) で地図の店に結ぶ。同じ店に結べる地図の店が 2 つ以上あれば、階のあるものの最初。
    // 戻り値: 状態の欄に出す知らせ (選べたときは null)
    public string? SelectShop(ShopTreeShop shop)
    {
        var m = _model;
        if (m == null) return $"「{shop.Name}」を出す地図がありません";
        var hits = m.Shops.Where(s => ShopOf(_tree, m.Record, s)?.Record == shop.Record).ToList();
        if (hits.Count == 0) return $"「{shop.Name}」は、この地図の店の中に見つかりませんでした (場所の地図だけ出しています)";
        var hit = hits.FirstOrDefault(s => s.Floor.Length > 0) ?? hits[0];
        if (_places.FirstOrDefault(p => p.Kind == "shop" && p.Index == hit.Index) is not { } place) return $"「{shop.Name}」は、この地図の店の中に見つかりませんでした (場所の地図だけ出しています)";
        _dest = place;
        txtDestFilter.Text = "";
        RefreshPlaceLists();
        string? note = null;
        if (hit.Floor.Length > 0)
        {
            SelectFloorInList(hit.Floor);
            object focus = new { type = "focus", pos = new[] { hit.Pos.X, hit.Pos.Y, hit.Pos.Z }, floor = hit.Floor, size = 40 };
            // ビューアの用意がまだなら、地図を送るとき (SendLoad) に寄せる (階は load の floor で開く)
            if (_viewerReady) { Send(new { type = "floor", id = hit.Floor }); Send(focus); }
            else _pendingFocus = focus;
        }
        else note = $"「{shop.Name}」は地図のどの階にも載っていないので、目的地にだけしました";
        SendEnds();
        _ = RouteAsync();
        return note;
    }
    // ビューアの用意の前に頼まれた、店に寄せる知らせ (SelectShop。SendLoad で送る)
    private object? _pendingFocus;

    private void SelectFloorInList(string fid)
    {
        if (_floor == fid) return;
        _floor = fid;
        // ビューアで移った階 (［上の階］［下の階］・現在位置の階・区画の階など) が同じ名前の階のまとまりの中なら、そのまとまりを開いて選ぶ
        RefreshFloors(RevealFloor(fid));
        RefreshSections();
    }

    // ===== 現在位置 =====

    // 今いるハンガー (ハンガーの中と判定したときの入口。推測のときは null)
    private StationMapModel.HangarInfo? _curHangar;

    // 出発地にするハンガーロビー: いるハンガーの入口にいちばん近い艦隊マネージャーの端末のまとまり (入口が分からなければ端末のいちばん多いまとまり)
    private StationMapModel.TerminalGroup? HangarLobby() => _model?.FleetLobby(_curHangar?.Pos);

    // ハンガーロビーの階に移って寄せる (ボタンで現在位置を取った・推測したとき)
    private void HangarFocus()
    {
        if (_cur is not { State: "Hangar" or "HangarGuess" } || HangarLobby() is not { } lob || lob.Floor.Length == 0) return;
        SelectFloorInList(lob.Floor);
        Send(new { type = "floor", id = lob.Floor });
        Send(new { type = "hlterm", index = lob.Index });
        Send(new { type = "focus", pos = new[] { lob.Pos.X, lob.Pos.Y, lob.Pos.Z }, floor = lob.Floor, size = 60 });
    }

    // (2026-11-02、指示 C) ボタンで座標が取れなかったとき、Game.log の手がかり (MapPanel.LastCaptureFailLog) から、この地図のステーションのハンガーにいる見込みが高ければ
    // 出発地をハンガーロビーにする。見込み: 今の着陸場所の名前がこの地図の場所の短い名前と同じで、
    //   ハンガー進入の通知の後に退出が無い (InHangar。無統治区域の通知の出る Pyro など)、または
    //   着陸場所に着いた後に、自分の船の一覧の取得 (ハンガーの端末) か船の呼び出しがある (Stanton。ハンガー進入の通知が出ないため。推測)。
    // できたら画面に出す文を返す。できなければ null
    // (2026-11-02、検証の指摘「中 4」) 手がかり (ハンガー進入・自分の船の一覧の取得・船の呼び出し) は、取得に失敗した時刻から HangarClueWindow 以内のものだけ
    // (ASOP を使った後に店へ歩いた人をハンガーにしないため)。推測の結果は、次に座標を取るまで残す (s_hangarGuess。地図を開き直しても出る)
    private static readonly TimeSpan HangarClueWindow = TimeSpan.FromMinutes(15);
    // 推測した地図 (record) と、そのときの現在位置の時刻 (App.Config.MapCurrent.Utc。これが変わったら (座標を取ったら) 推測をやめる)
    private static (string Record, double CurrentUtc)? s_hangarGuess;

    private string? TryHangarFromGameLog()
    {
        var m = _model;
        if (m == null || MapPanel.LastCaptureFailLog is not { } hint) return null;
        if (hint.Landing is not { Length: > 0 } landing || !string.Equals(landing, StationMapExports.ShortName(m.Record), StringComparison.OrdinalIgnoreCase)) return null;
        var q = hint.Quantum;
        bool Recent(DateTime? t) => t is { } v && v <= hint.At.AddMinutes(1) && hint.At - v <= HangarClueWindow;
        var likely = q is { InHangar: true } && Recent(q.LastHangarEnter)
            || q is { LandingSince: { } since } && (Recent(q.LastOwnFetch) && q.LastOwnFetch >= since || Recent(q.LastSpawn) && q.LastSpawn >= since);
        if (!likely) return null;
        _curHangar = null;
        if (HangarLobby() is not { } lob) return null;
        _cur = (lob.Pos, lob.Floor, "HangarGuess");
        s_hangarGuess = (m.Record, App.Config.MapCurrent.Utc);
        App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの地図] 現在位置を取れなかったので、Game.log からハンガー（推測）として出発地にしました: 着陸場所 {landing}、" +
            $"ハンガー進入の後 {(q?.InHangar == true ? "退出なし" : "-")}、着陸 {q?.LandingSince:HH:mm:ss}、船の一覧の取得 {q?.LastOwnFetch:HH:mm:ss}、呼び出し {q?.LastSpawn:HH:mm:ss}");
        return "現在位置は取れませんでした。ゲームのログからハンガー（推測）として出発地にしました";
    }

    private void UpdateCurrent(bool send = true)
    {
        var m = _model;
        if (m == null) return;
        var cur = App.Config.MapCurrent;
        _curUtc = cur.Utc;
        _cur = null;
        _curHangar = null;
        txtCurrent.Text = "";
        // Game.log からハンガーと推測した後、まだ座標を取っていなければ、その推測を出す (TryHangarFromGameLog)
        if (s_hangarGuess is { } guess && guess.Record == m.Record && guess.CurrentUtc == cur.Utc && HangarLobby() is { } glob)
        {
            _cur = (glob.Pos, glob.Floor, "HangarGuess");
            txtCurrent.Text = "現在位置は取れませんでした。ゲームのログからハンガー（推測）として出発地にしました";
            if (send) SendCurrent();
            return;
        }
        if (s_hangarGuess is { } old && old.CurrentUtc != cur.Utc) s_hangarGuess = null;
        if (cur.Utc <= 0) { if (send) SendCurrent(); return; }
        var global = (cur.X, cur.Y, cur.Z);
        var node = _tree?.NodeByStarmapRecord(m.Record);
        if (m.RootInfo is not { } root)
        {
            txtCurrent.Text = "この地図は前の版で取り込んだもので、現在位置を出すのに要る値がありません。取り込み直しが要ります";
            if (send) SendCurrent();
            return;
        }
        double theta = 0;
        IReadOnlyList<MapSpinCorrection> corrections = App.Config.MapSpinCorrections;
        var corrNote = "";
        if (root.Spins && _spinBody is { Length: > 0 } cb) corrections = CorrectionsFor(cur.Utc, cb, out corrNote);
        if (root.Spins)
        {
            // 都市: 取った時刻 (秒未満まで) の自転の角度 (StationRoot.CorrectedTheta。データの式 + その天体の補正値 (CorrectionsFor))
            if (_spinBody is not { Length: > 0 } body || root.CorrectedTheta(cur.Utc, body, corrections) is not { } th)
            {
                txtCurrent.Text = _spinBody is { Length: > 0 } ? "自転の角度が分からないので、現在位置を出せません" : "自転する天体が星図に見つからないので、現在位置を出せません";
                if (send) SendCurrent();
                return;
            }
            theta = th;
        }
        // 現在位置の星系 (いちばん近い場所の星系)。この場所の星系でなければ外
        var res = _tree != null ? CurrentLocation.Locate(_tree, global, cur.Utc, MapPanel.SystemNames, null, fresh: false) : null;
        var sameSystem = res == null || node == null || res.System == node.Root;
        // 自転する場所: 取った時刻の前後 1.5 秒で時刻をずらし、点がどれかの階の床の上 (StationMapModel.FitStanding) になる時刻の位置を使う。
        // 1 秒のずれで点が数百 m〜2 km 動く (Seraphim で 1 秒に約 2066 m) ため。見つからなければ取った時刻のまま (画面の文に添える)
        StationMapModel.StandFit? fit = null;
        var fitNote = "";
        if (root.Spins && sameSystem && _spinBody is { Length: > 0 } fitBody)
        {
            fit = m.FitStanding(global, dt => root.CorrectedTheta(cur.Utc + dt, fitBody, corrections));
            // (2026-11-02、利用者の指摘) 失敗の文は利用者に分かる言葉にする (ハンガーの中は FitStanding が床の次に探す)
            if (fit == null) fitNote = "（位置を床の上に合わせられませんでした。地図に床の無い所 (ハンガーの中・船の中など) かもしれません）";
        }
        fitNote = corrNote + fitNote;
        LogFit(m, cur.Utc, fit, root.Spins && sameSystem, corrections, _spinBody);
        var local = fit?.Local ?? m.ToLocal(global, theta)!.Value;
        _curHangar = null;
        // (2026-11-02、利用者の指摘「ハンガー内にいると現在位置がとれない」) ハンガーの中 (時刻合わせがハンガーの中で合った、または区画の中でなくハンガーの範囲に入る。
        // StationMapModel.HangarAt): 「ハンガーの中にいます」とし、道順の出発地はそのハンガーにいちばん近いハンガーロビー (艦隊マネージャーの端末) にする
        // 自転しない場所 (時刻合わせをしない) は、取った点でハンガーの中か (HangarPointAt: 床の上でなく、小さい区画の箱の外で、ハンガーの範囲) を見る
        var hangar = sameSystem ? fit?.Hangar ?? (fit == null ? m.HangarPointAt(local) : null) : null;
        if (hangar != null)
        {
            _curHangar = hangar;
            _cur = (local, "", "Hangar");
            txtCurrent.Text = $"ハンガーの中にいます（ハンガー ({(hangar.Size.Length > 0 ? hangar.Size : "?")}) の下）。道順はハンガーロビーから出します" + corrNote;
        }
        else if (sameSystem && m.SectionAt(local) != null)
        {
            _cur = (local, fit?.Floor?.Id ?? m.FloorOf(local)?.Id ?? "", "Inside");
            txtCurrent.Text = fitNote;
        }
        else if (sameSystem && (fit != null || m.InsideBounds(local)))
        {
            _cur = (local, fit?.Floor?.Id ?? m.FloorOf(local)?.Id ?? "", "Bounds");
            txtCurrent.Text = "現在位置は区画の外です（ハンガーなど）。道順は到着口のエレベーターから出します" + fitNote;
        }
        else
        {
            _cur = (local, "", "Outside");
            var near = NearestStation(res, cur.Utc);
            txtCurrent.Text = (near is { } n
                ? $"ステーションの外です（いちばん近いステーション: {n.Name}・{GameDataQueryService.FormatDistance(n.Distance)}）"
                : "ステーションの外です") + fitNote;
        }
        if (send) SendCurrent();
    }

    // 時刻合わせの結果をアプリのログに出す (同じ地図・同じ取った時刻では 1 回だけ): 取った時刻 (ミリ秒まで)・合わせた δ・入った階・区画
    private (string Record, double Utc)? _fitLogged;
    // 使う自転の補正値: 今の現在位置 (時刻 utc) で測り直した補正値 (CurrentLocation.Locate が「点 = 基準の場所 (Game.log の居場所など) の中心」とみなして測ったもの)
    // があれば、それは使わず、その前の補正値 (CurrentLocation.PreviousCorrections) に差し替える。前の値が無ければ (アプリを起動してから 1 回目の測り直しで、
    // その前に補正値が無かったとき)、測り直した値をそのまま使い、note に知らせの文を入れる
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

    private void LogFit(StationMapModel m, double utc, StationMapModel.StandFit? fit, bool tried, IReadOnlyList<MapSpinCorrection> corrections, string? body)
    {
        if (_fitLogged is { } done && done.Record == m.Record && done.Utc == utc) return;
        _fitLogged = (m.Record, utc);
        var taken = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(utc * 1000)).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff");
        var head = $"[ステーションの地図] 現在位置（{m.Record}）: 取った時刻 {taken}";
        string text;
        if (!tried) text = "時刻合わせはしていません (自転しない場所か、別の星系)";
        else if (fit is not { } f) text = $"時刻合わせ: 床の上になる時刻が前後 {StationMapModel.FitWindowSeconds} 秒に無いので、取った時刻のまま";
        else
        {
            var floorName = f.Floor == null ? $"ハンガーの中 (入口 {f.Hangar?.Index} {f.Hangar?.Size})" : _floorDisplay.TryGetValue(f.Floor.Id, out var fn) ? fn : f.Floor.Id;
            var secName = f.Section is { } s ? (_sectionNames.TryGetValue(s.Id, out var sn) ? sn : s.Name) + $" ({s.Id})" : "なし";
            text = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"δ {f.Delta:+0.000;-0.000} 秒、局所 ({f.Local.X:0.0}, {f.Local.Y:0.0}, {f.Local.Z:0.0})、階 {floorName} ({f.Floor?.Id ?? "-"})、区画 {secName}、候補 {f.Candidates}");
        }
        var corr = body is { Length: > 0 } && corrections.LastOrDefault(c => c.Body == body) is { } cc
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"、補正値 {cc.OffsetDeg:0.######}° (測った時刻 {DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(cc.Utc * 1000)).ToLocalTime():HH:mm:ss.fff}{(cc.Utc == utc ? "。今回の点で測ったもの" : "")})")
            : "";
        App.AppendStartupLog(App.Config.WorkingDirectory, head + "、" + text + corr);
    }

    // 自転する天体の星図の record 名 (StationRoot.CorrectedTheta の bodyRecord。SpinUuid の場所の record。gamedata_cache.db の starmap_locations を読み取り専用で引く)。
    // 無ければ null
    private static string? RecordOfUuid(string uuid)
    {
        var db = DetailPanel.DbPath();
        if (!File.Exists(db)) return null;
        using var conn = new SqliteConnection($"Data Source={db};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT record_name FROM starmap_locations WHERE lower(uuid) = $u LIMIT 1";
        cmd.Parameters.AddWithValue("$u", uuid.ToLowerInvariant());
        return cmd.ExecuteScalar() as string;
    }

    private static (string Name, double Distance)? NearestStation(CurrentLocation.Result? res, double unix)
    {
        if (res == null) return null;
        // Locate の Near は近い順の 10 か所だけなので、星系の全部の場所からステーション・都市 (Manmade 系・LandingZone) を探す
        var all = Descendants(res.System).Where(n => n.Position.HasValue).ToList();
        var thetas = CurrentLocation.ThetasAt(all, unix);
        var cur = (App.Config.MapCurrent.X, App.Config.MapCurrent.Y, App.Config.MapCurrent.Z);
        (string, double)? best = null;
        foreach (var n in all.Where(n => n.Type.StartsWith("Manmade", StringComparison.Ordinal) || n.Type == "LandingZone"))
        {
            var p = CurrentLocation.PlacePosition(n, thetas);
            var d = Math.Sqrt((p.X - cur.X) * (p.X - cur.X) + (p.Y - cur.Y) * (p.Y - cur.Y) + (p.Z - cur.Z) * (p.Z - cur.Z));
            if (best == null || d < best.Value.Item2) best = (n.Name, d);
        }
        return best;
    }

    private static IEnumerable<ShopTreeNode> Descendants(ShopTreeNode n)
    {
        foreach (var c in n.Children)
        {
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    // ===== 道順 =====

    // 出発地・目的地の節点 (無ければ -1) と、目的地の名前
    private int NodeOf(Place p, bool isStart, out string name)
    {
        var m = _model!;
        name = p.Display;
        switch (p.Kind)
        {
            case "shop":
                {
                    var s = m.Shops[p.Index];
                    name = _shopNames[p.Index].Length > 0 ? _shopNames[p.Index] : "店";   // 手順の文の終わり (名前の無い店は「店」)
                    return s.Node >= 0 ? s.Node : m.NearestNode(s.Pos);
                }
            case "transit":
                {
                    var t = m.Transit[p.Index];
                    name = _hangarLifts.Contains(p.Index) ? HangarLift : _transitNames.TryGetValue(p.Index, out var n) ? n : (t.Kind == "tram" ? "シャトル乗り場" : "エレベーター");
                    return t.Node >= 0 ? t.Node : m.NearestNode(t.Pos);
                }
            case "terminal":
                {
                    var g = m.TerminalGroups[p.Index];
                    name = TerminalLabel(g);
                    return g.Node >= 0 ? g.Node : m.NearestNode(g.Pos);
                }
            case "section":
                {
                    var s = m.Sections[p.Index];
                    name = _sectionNames[s.Id];
                    return m.SectionPoint(s, _floor) is { } pt ? m.NearestNode(pt) : -1;
                }
            case "pin":
                name = "ピン";
                return _pin is { } pin ? m.NearestNode(pin.Pos) : -1;
            case "current":
                name = "現在位置";
                if (_cur is not { } c) return -1;
                if (c.State == "Inside") return m.NearestNode(c.Pos);
                // 区画の外・ステーションの外: いちばん近い到着口 (ハンガー・ドッキングのエレベーター。ハンガーのエレベーター (HangarLifts) も入れる)
                // ハンガーの中 (推測を含む): ハンガーロビー (艦隊マネージャーの端末のまとまり) の節点。無ければ下の到着口
                if (c.State is "Hangar" or "HangarGuess" && HangarLobby() is { } lobby)
                    return lobby.Node >= 0 ? lobby.Node : m.NearestNode(lobby.Pos);
                var arrivals = m.Transit.Where(t => t.Node >= 0 && (_hangarLifts.Contains(t.Index) || t.Kind == "elevator" && _transitNames.TryGetValue(t.Index, out _)
                    && (t.Dest.Contains("docking", StringComparison.OrdinalIgnoreCase) || t.Dest.Contains("hangar", StringComparison.OrdinalIgnoreCase)))).ToList();
                if (arrivals.Count == 0) return -1;
                var a = arrivals.MinBy(t => Math.Pow(t.Pos.X - c.Pos.X, 2) + Math.Pow(t.Pos.Y - c.Pos.Y, 2) + Math.Pow(t.Pos.Z - c.Pos.Z, 2))!;
                return a.Node;
        }
        return -1;
    }

    // withCurrent: 道順に寄せるとき、現在位置の印も入れる (ボタンで現在位置を取ったとき)。
    // fit: 出した道順に寄せるか (自動の取得で出し直すときは寄せない。画面を勝手に動かさない)
    private async Task RouteAsync(bool withCurrent = false, bool fit = true)
    {
        var m = _model;
        var version = ++_routeVersion;
        if (m == null || _start == null || _dest == null) return;
        var from = NodeOf(_start, true, out _);
        var to = NodeOf(_dest, false, out var target);
        if (from < 0 || to < 0)
        {
            txtRoute.Text = from < 0 ? (_start.Kind == "current" ? "現在位置が分からないので、道順を出せません" : "出発地の位置が分かりません") : "目的地の位置が分かりません";
            lstSteps.ItemsSource = null;
            Send(new { type = "route", pts = (object?)null });
            return;
        }
        txtRoute.Text = "道順を計算しています...";
        StationMapModel.RouteResult? r;
        List<string>? lines = null;
        try
        {
            var names = new Dictionary<string, string>(_floorStepName, StringComparer.Ordinal);
            // 乗り場の行き先は、手順の文でも画面と同じ英語の名前 (キーのままは出さない)。引けない乗り場は種類の名前 (シャトル乗り場・エレベーター)
            var stops = new Dictionary<(int, string), string>();
            foreach (var t in m.Transit.Where(t => t.Node >= 0))
                stops[(m.Graph.Nodes[t.Node].Instance, m.Graph.Nodes[t.Node].Guid)] = _stopNames.TryGetValue(t.Index, out var tn) ? tn : (t.Kind == "tram" ? "シャトル乗り場" : "エレベーター");
            r = await Task.Run(() => m.Route(from, to, target, names, stops));
            if (r != null) lines = r.Steps.Select(s => m.StepText(s, stops)).Append(target).ToList();
        }
        catch (Exception ex)
        {
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの地図] 道順を計算できませんでした: {ex}");
            r = null;
        }
        if (version != _routeVersion || m != _model) return;
        if (r == null)
        {
            txtRoute.Text = "道がつながっていないので、道順を出せません";
            lstSteps.ItemsSource = null;
            Send(new { type = "route", pts = (object?)null });
            return;
        }
        txtRoute.Text = r.Summary;
        lstSteps.ItemsSource = lines;
        Send(new { type = "route", pts = r.Points.Select(p => new object[] { Math.Round(p.X, 1), Math.Round(p.Y, 1), Math.Round(p.Z, 1), p.Floor }).ToList(),
            trams = r.TramLegs ?? new List<int>(),
            // 乗った区間: [道筋の番号, 乗る乗り場の x, y, 降りる乗り場の x, y] (全体図で、道筋のうちこの間だけを太くする)
            tramSpans = (r.TramSpans ?? new()).Select(s => new double[] { s.Track, Math.Round(s.AX, 1), Math.Round(s.AY, 1), Math.Round(s.BX, 1), Math.Round(s.BY, 1) }).ToList(),
            fit, withCur = withCurrent });
    }
}
