using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace StarCitizenJapaneseTextCreater;

// ［詳細］タブ。船・車両・武器・装備の詳細を出す (GameDataQueryService.GetItemDetailText を Markdown の表にして WebView2 で出す)。
//   名前の欄・分類・種類・サイズ: どれを変えても下のリストを絞り込む。名前の入力は 0.3 秒待ってから絞り込む。
//     分類が (すべて): 名前で item_index をあいまい検索する (GameDataQueryService.SearchDetailCandidates。船・車両を含む全部)。
//       星系・場所・お店・資源の名前の部分一致の候補を、品目の候補の前に足す (星系 → 場所 → お店 → 資源 → 品目)。
//       名前が空ならリストは空。種類・サイズは使わない (空のリスト)
//     分類が 船舶: 船・車両 (GetDetailShipList。比べるツールの船の一覧と同じ規則)。種類は船の種別 (ゲーム内の分類) を大きさでまとめたもの、サイズは船のサイズ
//     分類が 船舶武器 / 船舶装備 / 個人武器 / 個人装備: ［船舶戦闘］［船舶装備］［個人戦闘］［個人装備］の表と同じ品目
//       (GetShipWeaponTable / GetShipGearTable / GetPersonalWeaponTable / GetPersonalGearTable) を、種類・サイズ・名前 (部分一致) で絞り込む。
//       種類・サイズの選択肢はその品目から作る (個人武器・個人装備のサイズは空のリスト)
//     分類が お店 / ステーション / 星系: インデックス構築で作る星図の場所・ゲームの店の表と、店に紐づけた DB の価格 (GameDataQueryService.BuildShopTree) の店・場所・星系。
//       詳細は record が "Shop:" / "Place:" / "System:" で始まるもの (GameDataQueryService.ShopTreeDetailText)
//   リストで選ぶと詳細を出す。名前の欄で ↓ を押すとリストへ、Enter でリストの最初の品目を開く
//   詳細の中のリンク ([[record|表示名]]。DetailHtml が scjt://detail/… の <a> にする) を押すと、その record の詳細を開く
//   戻る: リストで選んで詳細が変わったとき・リンクで移ったとき・外からの要求 (Request / RequestList / RequestSearch)・分類を変えたときに、
//     移る前の状態を履歴 (最大 10 件) に積み、［戻る］でその状態をそのまま出す
//   表の名前のダブルクリック: DetailPanel.Request(record, 名前) → MainWindow が［詳細］タブに切り替えて ShowItem を呼ぶ
//   record が決まらないとき (船舶管理の船の一覧の船名): DetailPanel.RequestSearch(名前) → MainWindow が［詳細］タブに切り替えて ShowSearch を呼ぶ
// DB (gamedata_cache.db) は読み取り専用で開く。販売場所は保存データ (cstone / UEX) で出し、無ければ UEX の値段を裏で取ってから出し直す
public partial class DetailPanel : UserControl
{
    // 表から［詳細］を開く要求 (record 名。無ければ null、名前)。MainWindow が受ける
    public static event Action<string?, string>? Requested;
    public static void Request(string? record, string name) => Request(record, name, null);
    // shipKey: どの所持船の詳細か (ship_loadouts のキー。［所持船］の行から開いたとき)。この詳細の表示から開いた船の 3D は、その所持船の塗装で開く (2026-11-05)。
    // 頼みの品 (record) と組で覚え、ShowItem がその品を出すときだけ使う (ほかの道で詳細を出したら捨てる。LoadDetailAsync)
    private static (string Record, string Key)? s_requestShipKey;
    public static void Request(string? record, string name, string? shipKey)
    {
        var rec = string.IsNullOrWhiteSpace(record) ? null : record;
        s_requestShipKey = rec != null && shipKey != null ? (rec, shipKey) : null;
        Requested?.Invoke(rec, name ?? "");
    }
    // 今の詳細の表示の所持船 (品と ship_key)。次に出す詳細で使うもの (_ownedNext) は ShowItem が入れ、LoadDetailAsync が今のものにする (入っていなければ消す)
    private (string Record, string Key)? _ownedShip, _ownedNext;
    // 今の詳細の表示が、その品の所持船から開いたものなら、その ship_key
    private string? OwnedShipKeyFor(string record) =>
        _ownedShip is { } o && string.Equals(o.Record, record, StringComparison.OrdinalIgnoreCase) ? o.Key : null;
    // 名前欄にその名前を入れて検索した状態で［詳細］を開く要求 (名前)。MainWindow が受ける
    public static event Action<string>? SearchRequested;
    public static void RequestSearch(string name) => SearchRequested?.Invoke(name ?? "");
    // 候補の一覧をリストに並べ、その中の 1 つを選んだ状態で［詳細］を開く要求 (候補 (record 名, 名前, 資源か), 選ぶ番号)。MainWindow が受ける。
    // ［BP］タブの材料の欄のダブルクリック (その BP の材料を全部並べる) から使う
    public static event Action<IReadOnlyList<(string Record, string Name, bool IsResource)>, int>? ListRequested;
    public static void RequestList(IReadOnlyList<(string Record, string Name, bool IsResource)> items, int selectIndex)
    {
        if (items == null || items.Count == 0) return;
        ListRequested?.Invoke(items, selectIndex);
    }

    // 分類 (表と同じ分け方)。(すべて) は名前で全部から探す
    private const string CategoryAll = "(すべて)";
    private const string CategoryShip = "船舶";
    private const string CategoryShipWeapon = "船舶武器", CategoryShipGear = "船舶装備", CategoryPersonalWeapon = "個人武器", CategoryPersonalGear = "個人装備";
    // 鉱石: 採掘で取れる資源とその精錬後、鉱石・鉱物・金属などの group の資源 (GameDataQueryService.GetOreCategoryResources)。種類は group、サイズは使わない
    private const string CategoryOre = "鉱石";
    // お店・ステーション・星系: 星図の場所 (starmap_locations)・ゲームの店 (game_shops)・店と DB の価格の紐づけ (shop_price_links) から作る (GameDataQueryService.BuildShopTree)。
    // サイズは使わない。行の作り方は TreeCategoryRows
    private const string CategoryShop = "お店", CategoryStation = "ステーション", CategorySystem = "星系";
    private const string AnyValue = "(すべて)";
    private const string LoadingText = "読み込み中...";

    // リストの 1 行 (Record が空なら名前で引き直す)
    // Depth / ChildCount / IsOpen: 一覧で星図の場所の親子をまとめたときの段 (0 = いちばん上)・まとめた子の数 (0 なら親の行でない)・開いているか (GroupedRows)
    private sealed record ListItem(string Record, string Name, string Kind, int Size, string Display, int Depth = 0, int ChildCount = 0, bool IsOpen = false);

    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool _suppressSearch, _suppressSelection;
    private int _searchVersion, _detailVersion, _categoryVersion;
    // 「読み込み中...」を出して進行中の読み込み (分類の一覧・詳細) の数。古い分類の読み込みが戻ったときに、進行中の読み込みの表示を消さないために使う
    private int _loadingCount;
    // 読み込みの進行中に「読み込み中...」の代わりに出すはずだった本来の表示 (件数・空など)。最後の読み込みが終わったときに出す (RestoreStatusIfIdle)
    private string _statusAfterLoading = "";
    // 分類ごとの品目 (一度読んだら覚えておく)
    private readonly Dictionary<string, List<ListItem>> _categoryRows = new();

    // WebView2 (使えなければ文字の欄で出す)。初期化は一度だけ (_webInit を覚えておき、2 回目以降は同じ Task を待つ)
    private bool _webReady, _webFailed;
    private string? _pendingHtml;
    private Task? _webInit;

    public DetailPanel()
    {
        InitializeComponent();
        cmbCategory.ItemsSource = new[] { CategoryAll, CategoryShip, CategoryShipWeapon, CategoryShipGear, CategoryPersonalWeapon, CategoryPersonalGear, CategoryOre,
                                          CategoryShop, CategoryStation, CategorySystem };
        _suppressSearch = true;
        cmbCategory.SelectedIndex = 0;
        _suppressSearch = false;
        SetFilterChoices(null);
        ShowListArea(false);
        _searchTimer.Tick += async (_, _) =>
        {
            _searchTimer.Stop();
            try { await RefreshListAsync(); }
            catch (Exception ex) { txtStatus.Text = $"検索できませんでした: {ex.Message}"; }
        };
        Loaded += async (_, _) => await (_webInit ??= InitWebViewAsync());
        // 品目名が作り直されたら、分類ごとの品目の覚えを次に表示されたとき (Loaded) に捨てて読み直す
        GameDataExtractor.ItemNamesChanged += () => _namesStale = true;
        // 星図の場所・店・紐づけの表が作り直されたら (起動後の補完・構築・価格の取得の後)、お店・場所の覚えと、その分類のリストの覚えをすぐ捨てる。
        // 次に詳細・分類・検索で使うときに今の表から作り直す (前は品目名の作り直しのときだけ捨てていたので、補完の前に作った座標の無い木を使い続けていた)
        GameDataExtractor.ShopTablesChanged += () => Dispatcher.BeginInvoke(() =>
        {
            _shopTreeTask = null;
            _categoryRows.Remove(CategoryShop);
            _categoryRows.Remove(CategoryStation);
            _categoryRows.Remove(CategorySystem);
        });
        Loaded += async (_, _) =>
        {
            if (!_namesStale) return;
            _namesStale = false;
            await ReloadAfterNamesChangedAsync();
        };
    }

    // 品目名が作り直された (GameDataExtractor.ItemNamesChanged) ので、次に表示されたときに分類の品目を読み直す
    private bool _namesStale;

    // 分類ごとの品目の覚えを捨て、今の分類を読み直して、選んでいる種類・サイズ・検索語のままリストを作り直す
    private async Task ReloadAfterNamesChangedAsync()
    {
        _categoryRows.Clear();
        _shopTreeTask = null;   // お店・場所の覚え (売っている物の品目名) も作り直す
        var category = cmbCategory.SelectedItem as string ?? CategoryAll;
        if (category != CategoryAll)
        {
            try { await LoadCategoryAsync(category); }
            catch (Exception ex) { ShowStatus($"読み込めませんでした: {ex.Message}"); return; }
        }
        await RefreshListSafeAsync();
    }

    // 作業ディレクトリ・DB の場所 (MapPanel も使う)
    internal static string WorkDir()
    {
        var workDir = App.Config.WorkingDirectory;
        if (string.IsNullOrEmpty(workDir)) workDir = AppDomain.CurrentDomain.BaseDirectory;
        return workDir;
    }

    internal static string DbPath() => Path.Combine(WorkDir(), "gamedata_cache.db");
    internal static string TradeDbPath() => Path.Combine(WorkDir(), "trade_cache.db");

    // ===== お店・ステーション・星系 =====

    // お店・場所・星系 (ShopTree。一度作ったら覚えておく。品目名が作り直されたら捨てる)。作るのは裏のスレッド
    private Task<ShopTree?>? _shopTreeTask;

    // お店・場所・星系。gamedata_cache.db が無ければ null (表が無ければ空の ShopTree)。作るのに失敗したら覚えを捨てて例外を投げる (次に呼んだときに作り直す)
    private async Task<ShopTree?> GetShopTreeAsync()
    {
        var task = _shopTreeTask ??= BuildShopTreeAsync();
        try { return await task; }
        catch
        {
            if (ReferenceEquals(_shopTreeTask, task)) _shopTreeTask = null;
            throw;
        }
    }

    private static Task<ShopTree?> BuildShopTreeAsync()
    {
        var db = DbPath();
        var trade = TradeDbPath();
        return Task.Run(() =>
        {
            if (!File.Exists(db)) return (ShopTree?)null;
            using var q = GameDataQueryService.OpenReadOnly(db);
            return q.BuildShopTree(trade);
        });
    }

    // 分類「お店」「ステーション」「星系」の行 (名前の順)
    //   お店: game_shops 全部と、場所だけ結べた価格の店 (名前は「販売データの店名（販売データ）」)。種類は店の種類、表示は「名前 (場所の名前)」
    //   ステーション: 星図の場所のうち、種類が Manmade・Manmade_VisibleOnInteraction・LandingZone のもの、または店が 1 つ以上あるもの。
    //     種類は星図の種類の日本語名、表示は「名前 (親の名前)」
    //   星系: 根の Star (Stanton / Pyro / Nyx)
    private static List<ListItem> TreeCategoryRows(ShopTree tree, string category)
    {
        IEnumerable<ListItem> rows = category switch
        {
            CategoryShop => tree.Shops.Select(s => new ListItem(s.Record, s.Name, s.Kind, 0, $"{s.Name} ({s.Location.Name})")),
            CategoryStation => tree.Places.Where(p => ShopTree.StationTypes.Contains(p.Type) || p.Shops.Count > 0)
                .Select(p => new ListItem(p.Record, p.Name, p.TypeLabel, 0, p.Parent != null ? $"{p.Name} ({p.Parent.Name})" : p.Name)),
            CategorySystem => tree.Systems.Select(s => new ListItem(s.Record, s.System, "", 0, s.System)),
            _ => Enumerable.Empty<ListItem>(),
        };
        return rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Display, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ===== WebView2 =====

    // 作業ディレクトリの webview2_detail を WebView2 の利用者データの置き場にする (船舶管理の RSI 同期の webview2 とは分ける)
    private async Task InitWebViewAsync()
    {
        if (_webReady || _webFailed) return;
        try
        {
            var folder = Path.Combine(WorkDir(), "webview2_detail");
            Directory.CreateDirectory(folder);
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: folder);
            await webDetail.EnsureCoreWebView2Async(env);
            var core = webDetail.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = true;
            // 中身は自分で作った HTML だけ。利用者の操作でほかへ移る・新しい窓を開くことはさせない。
            // 詳細の中のリンク (scjt://detail/<URL エンコードした record>。DetailHtml) は移動を止めて、その record の詳細を開く (戻るの履歴に積む)
            core.NavigationStarting += (_, e) =>
            {
                if (DetailHtml.TryParseLink(e.Uri, out var record))
                {
                    e.Cancel = true;
                    Dispatcher.BeginInvoke(() => OpenLink(record));
                    return;
                }
                if (e.IsUserInitiated) e.Cancel = true;
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            // 船でない品の 3D の小さな絵: ページからの知らせ (絵を押した) と、ページを出し直したときの差し込み直し (ShowItemThumbAsync)
            core.WebMessageReceived += OnDetailWebMessage;
            core.NavigationCompleted += (_, _) => _ = InjectThumbAsync();
            webDetail.DefaultBackgroundColor = System.Drawing.Color.White;
            _webReady = true;
            if (_pendingHtml != null) { core.NavigateToString(_pendingHtml); _pendingHtml = null; }
            // 場所の施設のアイコン (ゲームの svg) を裏で取り出して割り当てる。割り当てられるまでは施設を文字だけで出す (AmenityIcons)
            _ = MapAmenityIconsAsync(core);
        }
        catch (Exception ex)
        {
            // WebView2 が使えない (ランタイムが無い等) ときは、今までどおり文字の欄で出す
            _webFailed = true;
            webDetail.Visibility = Visibility.Collapsed;
            txtDetail.Visibility = Visibility.Visible;
            txtStatus.Text = $"表の表示 (WebView2) を使えません。文字で表示します: {ex.Message}";
            // 失敗の中身 (型・HResult・メッセージ・スタックを含む例外の全文) をアプリのログ (作業ディレクトリの app_log.txt) に残す
            AppendAppLog($"[詳細] WebView2 の初期化に失敗しました: {ex.GetType().FullName} (HResult 0x{ex.HResult:X8}): {ex}");
        }
    }

    // 施設のアイコンを WebView2 に割り当てる。割り当てられたら、今出している詳細の施設の文字 (span.amentext[data-icon]) を、
    // ページを作り直さずにアイコンに差し替える (詳細の位置・図の拡大はそのまま。DetailHtml.SwapAmenityIconsScript)。これから出す詳細は初めからアイコンで出す
    private bool _amenityIcons;
    private string? _shownText;
    private async Task MapAmenityIconsAsync(CoreWebView2 core)
    {
        if (!await AmenityIcons.MapAsync(core)) return;
        _amenityIcons = true;
        if (_shownText is { } t && t.Contains("<<amen:", StringComparison.Ordinal))
        {
            try { await core.ExecuteScriptAsync(DetailHtml.SwapAmenityIconsScript); }
            catch (Exception ex) { AppendAppLog($"[詳細] 施設のアイコンを差し込めませんでした: {ex.Message}"); }
        }
    }

    // 作業ディレクトリの app_log.txt に 1 行追記する (MainWindow.Log と同じファイル・同じ行の形。MainWindow.Log は private で呼べないため)。
    // 書き先は MainWindow.Log と同じ決め方 (App.Config.WorkingDirectory をそのまま使う。空なら今いるディレクトリからの相対パス)
    private static void AppendAppLog(string msg)
    {
        App.AppendStartupLog(App.Config.WorkingDirectory, msg);   // ほかの書き込みと重ならないよう App のロックを通す
    }

    // 詳細の文字 (GetItemDetailText の形) を出す。WebView2 では Markdown の表にして HTML で、使えなければ文字のまま。
    // 別の品目の詳細になったら、3D のボタンを隠し、3D 表示をやめて表に戻す (同じ船の出し直し (UEX の値段) ではそのまま)
    private void ShowDetail(string text)
    {
        if (!string.Equals(_detailRecord, _ship3DRecord, StringComparison.OrdinalIgnoreCase)) SetShip3DTarget(null, "");
        if (!string.Equals(_detailRecord, _stationDetailRecord, StringComparison.Ordinal)) SetStationTarget(null, null);
        txtDetail.Text = DetailHtml.ForTextBox(text);   // 文字の欄ではリンクは表示名だけ、図は 1 行の断り書き
        txtDetail.ScrollToHome();
        _shownText = text;
        if (_webFailed) return;
        var html = DetailHtml.FromDetailText(text, _amenityIcons);
        if (_webReady) webDetail.CoreWebView2.NavigateToString(html);
        else _pendingHtml = html;
    }

    // ===== 開く (表のダブルクリック・船名) =====

    // 品目を出す。分類を (すべて) にして名前で検索したリストを出し、record の品目の詳細を出す。
    // record が無ければ名前で検索し、名前が同じ (大文字小文字無視) 候補、無ければ最初の候補を出す
    public async void ShowItem(string? record, string name)
    {
        // 所持船から開いた頼みなら、その品の詳細を出すときにその所持船を覚える (ほかの頼みでは覚えない)
        _ownedNext = s_requestShipKey is { } rk && record != null && string.Equals(rk.Record, record, StringComparison.OrdinalIgnoreCase) ? rk : null;
        s_requestShipKey = null;
        PushHistory(CaptureState());
        _searchTimer.Stop();
        SetQueryAndCategoryAll(name);
        try
        {
            var items = await SearchAllAsync(name);
            if (items != null) SetList(items, selectRecord: record);
            if (record == null)
            {
                var pick = items?.FirstOrDefault(c => c.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) ?? items?.FirstOrDefault();
                if (pick == null) { _detailVersion++; _detailRecord = null; ShowDetail($"「{name}」が見つかりませんでした。"); return; }
                record = pick.Record;
                SelectInList(record);
            }
            await LoadDetailAsync(record);
        }
        catch (Exception ex) { _detailRecord = null; ShowDetail($"詳細を出せませんでした: {ex.Message}"); txtStatus.Text = ""; }
    }

    // 候補の一覧 (RequestList) をリストに並べ、selectIndex の候補を選んだ状態にして詳細を出す。名前の欄・分類は ShowItem と同じ扱い
    // (分類を (すべて) にし、名前の欄に選んだ候補の名前を入れる。検索はしない)。リストの行は検索の候補と同じ形 (DetailCandidate.Display):
    // 資源は「名前 [資源]」(鉱石の範囲に入らない資源は「名前 [コモディティ]」)、品目は item_index (無ければ items) の種類。同じ record は最初の 1 つだけ並べる。番号が範囲の外なら先頭を選ぶ
    public async void ShowList(IReadOnlyList<(string Record, string Name, bool IsResource)> items, int selectIndex)
    {
        if (items.Count == 0) return;
        if (selectIndex < 0 || selectIndex >= items.Count) selectIndex = 0;
        var pick = items[selectIndex];
        var distinct = items.Where(x => !string.IsNullOrWhiteSpace(x.Record)).DistinctBy(x => x.Record, StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count == 0) return;
        if (string.IsNullOrWhiteSpace(pick.Record)) pick = distinct[0];
        PushHistory(CaptureState());
        _searchTimer.Stop();
        SetQueryAndCategoryAll(pick.Name);
        try
        {
            var db = DbPath();
            var list = await Task.Run(() =>
            {
                var types = new string[distinct.Count];
                GameDataQueryService? q = File.Exists(db) ? GameDataQueryService.OpenReadOnly(db) : null;
                try
                {
                    // 資源は、鉱石の範囲の資源が「資源」、それ以外が「コモディティ」(検索の候補と同じ。GameDataQueryService.ResourceTypeLookup)
                    var resourceType = distinct.Any(x => x.IsResource) ? q?.ResourceTypeLookup() : null;
                    for (int i = 0; i < distinct.Count; i++)
                        types[i] = distinct[i].IsResource ? resourceType?.Invoke(distinct[i].Record) ?? GameDataQueryService.ResourceDetailType : q?.ItemTypeOf(distinct[i].Record) ?? "";
                }
                finally { q?.Dispose(); }
                return distinct.Select((x, i) =>
                {
                    var c = new DetailCandidate(x.Record, x.Name, types[i], "", "");
                    return new ListItem(c.Record, c.Name, c.ItemType, 0, c.Display);
                }).ToList();
            });
            SetList(list, selectRecord: pick.Record);
            await LoadDetailAsync(pick.Record);
        }
        catch (Exception ex) { _detailRecord = null; ShowDetail($"詳細を出せませんでした: {ex.Message}"); txtStatus.Text = ""; }
    }

    // 名前欄に name を入れて検索し、リストを出す (品目は開かない)。下の詳細欄の前の品目は消す
    public async void ShowSearch(string name)
    {
        PushHistory(CaptureState());
        _detailVersion++;
        _detailRecord = null;
        ShowDetail("");
        _searchTimer.Stop();
        SetQueryAndCategoryAll(name);
        try { await RefreshListAsync(); }
        catch (Exception ex) { txtStatus.Text = $"検索できませんでした: {ex.Message}"; }
    }

    private void SetQueryAndCategoryAll(string name)
    {
        _categoryVersion++;   // 読み込み中の分類があっても、その結果で種類・サイズを作り直さない
        _suppressSearch = true;
        txtQuery.Text = name;
        if (!Equals(cmbCategory.SelectedItem, CategoryAll)) cmbCategory.SelectedItem = CategoryAll;
        SetFilterChoices(null);
        _suppressSearch = false;
    }

    // ===== リンク・戻る =====

    // 詳細の中のリンク (DetailHtml の scjt://detail/…) で record の詳細を開く。移る前の状態を戻るの履歴に積む (同じ詳細へのリンクなら積まない)。
    // 名前の欄を移った先の名前にし、分類を (すべて) にする (検索は走らせない)。リストは、先頭に移った先の候補 1 件 (選んだ状態)、
    // その下にその名前での検索の結果 (SearchAllAsync。同じ record は除く) を並べる。移った先の候補が作れなければ、名前の欄・分類・リストはそのまま
    private async void OpenLink(string record)
    {
        if (string.IsNullOrWhiteSpace(record)) return;
        // 店名の右の「MAP」(GameDataQueryService.ShopMapLink): ［マップ］タブでその店の位置を出す (店の詳細の［マップに表示］と同じ)。詳細は開かない
        if (record.StartsWith(GameDataQueryService.ShopMapPrefix, StringComparison.Ordinal))
        {
            await ShowShopOnMapAsync(record[GameDataQueryService.ShopMapPrefix.Length..]);
            return;
        }
        if (!string.Equals(record, _detailRecord, StringComparison.OrdinalIgnoreCase)) PushHistory(CaptureState());
        _searchTimer.Stop();
        var version = ++_searchVersion;   // 進行中の検索の結果でリストを差し替えない
        try
        {
            var detail = LoadDetailAsync(record);
            var db = DbPath();
            var tree = await GetShopTreeOrNullAsync();
            var target = await Task.Run(() =>
            {
                if (!File.Exists(db)) return null;
                using var q = GameDataQueryService.OpenReadOnly(db);
                return q.CandidateOf(record, tree);
            });
            if (target != null && version == _searchVersion)
            {
                SetQueryAndCategoryAll(target.Name);
                var found = await SearchAllAsync(target.Name) ?? new List<ListItem>();
                if (version == _searchVersion)
                {
                    var list = new List<ListItem> { ToListItem(target) };
                    list.AddRange(found.Where(x => !x.Record.Equals(target.Record, StringComparison.OrdinalIgnoreCase)));
                    SetList(list, selectRecord: target.Record);
                    ShowStatus($"候補 {list.Count} 件");
                }
            }
            else if (target == null) SelectInList(record);
            await detail;
        }
        catch (Exception ex) { _detailRecord = null; ShowDetail($"詳細を出せませんでした: {ex.Message}"); txtStatus.Text = ""; }
    }

    // 戻るの履歴の 1 件: 分類・種類 (選択肢・選んでいるもの・使えるか)・サイズ (同じ)・名前の欄・リストの中身・選んでいる行・表示している詳細の record
    private sealed record HistoryState(string Category, List<string> KindChoices, string? Kind, bool KindEnabled,
        List<string> SizeChoices, string? Size, bool SizeEnabled, string Query, List<ListItem> Items, ListItem? Selected, string? DetailRecord,
        List<string>? OpenParents = null);

    private const int HistoryMax = 10;
    private readonly List<HistoryState> _history = new();
    // 表示している詳細の record (LoadDetailAsync で決まる。見つからない・空の表示なら null)
    private string? _detailRecord;

    // 今の状態。category を渡すとその分類 (分類を変えた直後に、変える前の分類で積むため)、
    // useSelectedOverride が true なら選んでいる行は selected (リストで選び直した直後に、選ぶ前の行で積むため)
    private HistoryState CaptureState(string? category = null, ListItem? selected = null, bool useSelectedOverride = false) => new(
        category ?? cmbCategory.SelectedItem as string ?? CategoryAll,
        (cmbKind.ItemsSource as IEnumerable<string>)?.ToList() ?? new List<string>(), cmbKind.SelectedItem as string, cmbKind.IsEnabled,
        (cmbSize.ItemsSource as IEnumerable<string>)?.ToList() ?? new List<string>(), cmbSize.SelectedItem as string, cmbSize.IsEnabled,
        txtQuery.Text,
        _listSource.ToList(),
        useSelectedOverride ? selected : lstCandidates.SelectedItem as ListItem,
        _detailRecord,
        _openParents.ToList());

    // 履歴に積む (最大 10 件。超えたら古いものから捨てる)
    private void PushHistory(HistoryState state)
    {
        _history.Add(state);
        while (_history.Count > HistoryMax) _history.RemoveAt(0);
        btnBack.IsEnabled = _history.Count > 0;
    }

    // 最後に積んだ状態をそのまま出す (リストは作り直さずに覚えた中身を出し、検索は走らせない)。戻る操作そのものは積まない
    private async void BtnBack_Click(object sender, RoutedEventArgs e)
    {
        if (_history.Count == 0) return;
        var s = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        btnBack.IsEnabled = _history.Count > 0;

        _searchTimer.Stop();
        _categoryVersion++;   // 読み込み中の分類があっても、その結果で種類・サイズ・リストを作り直さない
        _searchVersion++;     // 進行中の検索の結果でリストを差し替えない
        _suppressSearch = true;
        try
        {
            cmbCategory.SelectedItem = s.Category;
            cmbKind.ItemsSource = s.KindChoices;
            cmbKind.SelectedItem = s.Kind;
            cmbKind.IsEnabled = s.KindEnabled;
            cmbSize.ItemsSource = s.SizeChoices;
            cmbSize.SelectedItem = s.Size;
            cmbSize.IsEnabled = s.SizeEnabled;
            txtQuery.Text = s.Query;
        }
        finally { _suppressSearch = false; }
        SetList(s.Items, selectRecord: s.Selected?.Record, openParents: s.OpenParents);
        ShowStatus(s.Category == CategoryAll ? (s.Query.Trim().Length == 0 ? "" : s.Items.Count > 0 ? $"候補 {s.Items.Count} 件" : "候補なし") : $"{s.Items.Count} 件");

        Task detail;
        if (s.DetailRecord == null) { _detailVersion++; _detailRecord = null; ShowDetail(""); detail = Task.CompletedTask; }
        else detail = LoadDetailAsync(s.DetailRecord);

        // 分類の品目の覚えが無い (品目名の作り直しで捨てた) ときは、後で種類・名前を変えたときに絞り込めるように読み込んでおく (リストは変えない)
        if (s.Category != CategoryAll && !_categoryRows.ContainsKey(s.Category))
        {
            try { await LoadCategoryAsync(s.Category); } catch { }
        }
        try { await detail; }
        catch (Exception ex) { _detailRecord = null; ShowDetail($"詳細を出せませんでした: {ex.Message}"); txtStatus.Text = ""; }
    }

    // ===== リスト =====

    private void TxtQuery_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSearch) return;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private async void CmbCategory_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSearch) return;
        // 分類を変える前の状態を戻るの履歴に積む (分類は変える前のもの。種類・サイズ・リストはまだ前の分類のまま)
        if (e.RemovedItems.Count > 0 && e.RemovedItems[0] is string previousCategory) PushHistory(CaptureState(category: previousCategory));
        _searchTimer.Stop();
        var category = cmbCategory.SelectedItem as string ?? CategoryAll;
        var version = ++_categoryVersion;
        if (category == CategoryAll) { SetFilterChoices(null); await RefreshListSafeAsync(); return; }
        SetFilterChoices(null);
        // 分類を変えたので、前の表示 (前の分類の件数など) は本来の表示にしない (この読み込みの結果で決まる)
        _statusAfterLoading = "";
        txtStatus.Text = LoadingText;
        List<ListItem>? rows;
        _loadingCount++;
        try { rows = await LoadCategoryAsync(category); }
        catch (Exception ex)
        {
            _loadingCount--;
            ShowStatus($"読み込めませんでした: {ex.Message}");
            RestoreStatusIfIdle();
            return;
        }
        _loadingCount--;
        // 最後の読み込みなら「読み込み中...」を本来の表示に戻す。ほかの読み込み (分類・詳細) がまだ進行中なら、その「読み込み中...」を残す
        RestoreStatusIfIdle();
        if (version != _categoryVersion) return;
        if (rows == null) { ShowStatus("インデックスが未構築です (設定タブの［インデックス構築］)"); SetList(new List<ListItem>()); return; }
        SetFilterChoices(rows, sizes: category is CategoryShip or CategoryShipWeapon or CategoryShipGear);
        await RefreshListSafeAsync();
    }

    private async void CmbFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSearch) return;
        _searchTimer.Stop();
        await RefreshListSafeAsync();
    }

    // 状態の欄に本来の表示 (件数・空など) を出す。読み込み (分類の一覧・詳細) が進行中 (_loadingCount > 0) なら「読み込み中...」を残し、
    // 出す文は覚えておいて、最後の読み込みが終わったとき (RestoreStatusIfIdle) に出す
    private void ShowStatus(string text)
    {
        if (_loadingCount > 0) _statusAfterLoading = text;
        else txtStatus.Text = text;
    }

    // 進行中の読み込みが無くなったら、残っている「読み込み中...」を覚えておいた本来の表示にする (後から別の文が出ていれば、そのまま)
    private void RestoreStatusIfIdle()
    {
        if (_loadingCount == 0 && txtStatus.Text == LoadingText) txtStatus.Text = _statusAfterLoading;
    }

    private async Task RefreshListSafeAsync()
    {
        try { await RefreshListAsync(); }
        catch (Exception ex) { txtStatus.Text = $"検索できませんでした: {ex.Message}"; }
    }

    // 種類・サイズの選択肢を作る。rows が null なら空のリスト (分類が (すべて)・読み込み前)。sizes が false ならサイズは空のリスト
    private void SetFilterChoices(List<ListItem>? rows, bool sizes = false)
    {
        _suppressSearch = true;
        try
        {
            if (rows == null)
            {
                cmbKind.ItemsSource = Array.Empty<string>();
                cmbSize.ItemsSource = Array.Empty<string>();
                cmbKind.IsEnabled = cmbSize.IsEnabled = false;
                return;
            }
            // 種類は表の並び (最初に出てきた順)、サイズは小さい順
            cmbKind.ItemsSource = new[] { AnyValue }.Concat(rows.Select(r => r.Kind).Where(k => k.Length > 0).Distinct(StringComparer.Ordinal)).ToList();
            cmbKind.SelectedIndex = 0;
            cmbKind.IsEnabled = true;
            if (sizes)
            {
                cmbSize.ItemsSource = new[] { AnyValue }.Concat(rows.Where(r => r.Size > 0).Select(r => r.Size).Distinct().OrderBy(s => s).Select(s => $"S{s}")).ToList();
                cmbSize.SelectedIndex = 0;
                cmbSize.IsEnabled = true;
            }
            else
            {
                cmbSize.ItemsSource = Array.Empty<string>();
                cmbSize.IsEnabled = false;
            }
        }
        finally { _suppressSearch = false; }
    }

    // 分類の品目 (その表と同じ品目。覚えておく)。インデックスが無ければ null
    private async Task<List<ListItem>?> LoadCategoryAsync(string category)
    {
        if (_categoryRows.TryGetValue(category, out var cached)) return cached;
        // お店・ステーション・星系は ShopTree から作る (item_index は使わない)。gamedata_cache.db が無ければ null
        if (category is CategoryShop or CategoryStation or CategorySystem)
        {
            // 表 (starmap_locations 等) はインデックス構築で作るので、無ければ「インデックスが未構築です」と同じ扱い
            var tree = await GetShopTreeAsync();
            if (tree == null || tree.IsEmpty) return null;
            var treeRows = TreeCategoryRows(tree, category);
            _categoryRows[category] = treeRows;
            return treeRows;
        }
        var db = DbPath();
        var rows = await Task.Run(() =>
        {
            if (!File.Exists(db)) return null;
            using var q = GameDataQueryService.OpenReadOnly(db);
            // 鉱石は resource_types から作る (item_index は使わない)。選ぶと資源の詳細 (GetItemDetailText → ResourceDetailText)
            if (category == CategoryOre) return q.GetOreCategoryResources().Select(r => Item(r.Record, r.Name, r.Kind, 0)).ToList();
            if (!q.HasItemIndex()) return null;
            return category switch
            {
                // 船・車両: 種類は船の種別を大きさでまとめたもの (GetDetailShipList)
                CategoryShip => q.GetDetailShipList().Select(r => Item(r.Record, r.Name, r.Kind, r.Size)).ToList(),
                CategoryShipWeapon => q.GetShipWeaponTable().Select(r => Item(r.Record, r.Name, r.Kind, r.Size)).ToList(),
                CategoryPersonalWeapon => q.GetPersonalWeaponTable().Select(r => Item(r.Record, r.Name, r.Kind, r.Size)).ToList(),
                CategoryShipGear => q.GetShipGearTable().Select(r => Item(r.Record, r.Name, r.Kind, r.Size)).ToList(),
                // 個人装備の種類は、アーマー・服は部位まで (例 アーマー (ヘルメット))、それ以外は表の種類
                CategoryPersonalGear => q.GetPersonalGearTable().Select(r => Item(r.Record, r.Name, r.Part.Length > 0 ? $"{r.Kind} ({r.Part})" : r.Kind, 0)).ToList(),
                _ => new List<ListItem>(),
            };
        });
        if (rows != null) _categoryRows[category] = rows;
        return rows;
    }

    private static ListItem Item(string record, string name, string kind, int size) =>
        new(record ?? "", name ?? "", kind ?? "", size, $"{name}  [{kind}{(size > 0 ? $" / S{size}" : "")}]");

    // 名前・分類・種類・サイズでリストを作り直す
    private async Task RefreshListAsync()
    {
        var version = ++_searchVersion;
        var query = txtQuery.Text.Trim();
        var category = cmbCategory.SelectedItem as string ?? CategoryAll;
        if (category == CategoryAll)
        {
            if (query.Length == 0) { SetList(new List<ListItem>()); ShowStatus(""); return; }
            List<ListItem>? found;
            try { found = await SearchAllAsync(query); }
            catch (Exception ex) { txtStatus.Text = $"検索できませんでした: {ex.Message}"; return; }
            if (version != _searchVersion) return;
            found ??= new List<ListItem>();
            SetList(found);
            ShowStatus(found.Count > 0 ? $"候補 {found.Count} 件" : "候補なし");
            return;
        }
        if (!_categoryRows.TryGetValue(category, out var rows)) return;   // 読み込み中 (読み込み後に絞り込む)
        var kind = cmbKind.SelectedItem as string;
        var size = cmbSize.SelectedItem as string;
        var list = rows.Where(r =>
                (kind == null || kind == AnyValue || r.Kind == kind)
                && (size == null || size == AnyValue || $"S{r.Size}" == size)
                && (query.Length == 0 || r.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        SetList(list);
        ShowStatus($"{list.Count} 件");
    }

    // 分類 (すべて) の検索: 星系・場所・お店 (ShopTree。作れなければ出さない)、資源、item_index のあいまい検索 (船・車両・武器・装備のすべて)。DB が無ければ null
    private async Task<List<ListItem>?> SearchAllAsync(string query)
    {
        var db = DbPath();
        var tree = await GetShopTreeOrNullAsync();
        return await Task.Run(() =>
        {
            if (!File.Exists(db)) return null;
            using var q = GameDataQueryService.OpenReadOnly(db);
            return q.SearchDetailCandidates(query, shopTree: tree).Select(ToListItem).ToList();
        });
    }

    private static ListItem ToListItem(DetailCandidate c) => new(c.Record, c.Name, c.ItemType, 0, c.Display);

    // 星系・場所・お店 (ShopTree)。作れなければ null (検索・リンクの候補にだけ使うので、失敗は知らせない)
    private async Task<ShopTree?> GetShopTreeOrNullAsync()
    {
        try { return await GetShopTreeAsync(); }
        catch { return null; }
    }

    // リストを差し替える (選択を変えても詳細は開き直さない)。selectRecord があればその行を選んだ状態にする
    // (その行が閉じた親の行の中なら、親を開いて見えるようにする)。
    // 星図の場所の親子は、親と子が両方とも候補にあれば子を親の下にまとめる (GroupedRows)。履歴にはまとめる前の候補 (_listSource) を積む
    // openParents: 開いておく親の行の record (戻るの履歴で、戻る前の開け閉めの状態を戻すため)
    private void SetList(List<ListItem> items, string? selectRecord = null, IEnumerable<string>? openParents = null)
    {
        _listSource = items;
        _openParents.Clear();
        if (openParents != null) _openParents.UnionWith(openParents);
        ShowRows(selectRecord);
        ShowListArea(items.Count > 0);
        RegroupWhenTreeReady();
    }

    // 一覧を出した時点で木 (_shopTreeTask) ができていなくてまとめられなかったときは、木ができたら今の一覧をまとめ直す
    // (その間に一覧が差し替わっていれば何もしない。選んでいる行とスクロールの位置は保つ)。お店・場所・星系の行が無ければ待たない
    private async void RegroupWhenTreeReady()
    {
        if (_shopTreeTask is { IsCompletedSuccessfully: true }) return;
        var source = _listSource;
        if (!source.Any(i => ShopTree.IsTreeRecord(i.Record))) return;
        var tree = await GetShopTreeOrNullAsync();
        if (tree == null || !ReferenceEquals(source, _listSource)) return;
        ShowRows((lstCandidates.SelectedItem as ListItem)?.Record, keepScroll: true);
    }

    // 一覧のスクロールの部品 (ListBox の中の ScrollViewer。まだ無ければ null)
    private ScrollViewer? ListScrollViewer()
    {
        if (System.Windows.Media.VisualTreeHelper.GetChildrenCount(lstCandidates) == 0) return null;
        var border = System.Windows.Media.VisualTreeHelper.GetChild(lstCandidates, 0);
        return border != null && System.Windows.Media.VisualTreeHelper.GetChildrenCount(border) > 0
            ? System.Windows.Media.VisualTreeHelper.GetChild(border, 0) as ScrollViewer : null;
    }

    // ===== 星図の場所の親子のまとめ (2026-11-02、利用者の決定「それで」: 子の場所を親の下にまとめて ▼ で開く) =====
    // 例: HUR L2 (Stanton1_L2) の下の HUR L2-A (Stanton1_L2_01)、HUR-L1 Green Glade Station (RR_HUR_L1) の下の HUR-L1 Green Glade Clinic (RR_HUR_L1_CLINIC)。
    // 親子は星図の parent_record (ShopTreeNode.Parent) で決める (名前では決めない)。子の親は、候補の中でいちばん近い祖先。
    // 親の行は「▼ 表示（N 件）」(閉じている。押すと開く)・「▲ …」(開いている。押すと閉じる)、子の行は一段下げる (［マップ］の階の一覧・StationMapWindow.GroupRows と同じ見た目と動き)。
    // 親の行も子の行も、選べば今までどおりその場所の詳細を出す。親の行は押すたびに開け閉めする。子だけが候補にあるときは今までどおり 1 行
    private List<ListItem> _listSource = new();
    private readonly HashSet<string> _openParents = new(StringComparer.Ordinal);

    // 今の候補 (_listSource) を、開け閉めの状態でまとめて一覧に出す。selectRecord があればその行を選ぶ (一覧に出ていなければ何も選ばない)。
    // reveal: selectRecord の行が閉じた親の中なら、親を開いて見えるようにする。keepScroll: 作り直す前のスクロールの位置に戻す。
    // focusSelected: 作り直した後、選んだ行 (無ければ一覧) に焦点を戻す (行の部品ができてから。←/→ の開け閉め)
    private void ShowRows(string? selectRecord = null, bool reveal = true, bool keepScroll = false, bool focusSelected = false)
    {
        var tree = _shopTreeTask is { IsCompletedSuccessfully: true } t ? t.Result : null;
        if (reveal && selectRecord != null && tree != null && tree.NodesByRecord.TryGetValue(selectRecord, out var target))
            for (var n = target.Parent; n != null; n = n.Parent) _openParents.Add(n.Record);
        var rows = GroupedRows(_listSource, tree);
        var offset = keepScroll ? ListScrollViewer()?.VerticalOffset : null;
        _suppressSelection = true;
        try
        {
            lstCandidates.ItemsSource = rows;
            lstCandidates.SelectedItem = selectRecord == null ? null
                : rows.FirstOrDefault(i => i.Record.Equals(selectRecord, StringComparison.OrdinalIgnoreCase));
        }
        finally { _suppressSelection = false; }
        if (offset == null && !focusSelected) return;
        // 行の部品を作らせてから位置・焦点を戻す (ItemsSource を差し替えた直後は部品がまだ無い)
        lstCandidates.UpdateLayout();
        if (offset is { } off) ListScrollViewer()?.ScrollToVerticalOffset(off);
        if (!focusSelected) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (lstCandidates.SelectedItem is { } sel && lstCandidates.ItemContainerGenerator.ContainerFromItem(sel) is ListBoxItem item) item.Focus();
            else lstCandidates.Focus();
        });
    }

    // 候補のまとめ: 星図の場所の行のうち、候補の中に祖先がある行を、いちばん近い祖先の行の下に移す (並びは元の順。親の行は元の位置)。
    // 閉じている親の下の行は出さない。木が無ければ (作れていない) まとめない
    private List<ListItem> GroupedRows(List<ListItem> items, ShopTree? tree)
    {
        if (tree == null) return items;
        var inList = items.Select(i => i.Record).Where(r => tree.NodesByRecord.ContainsKey(r)).ToHashSet(StringComparer.Ordinal);
        if (inList.Count < 2) return items;
        var parentOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in inList)
            for (var n = tree.NodesByRecord[r].Parent; n != null; n = n.Parent)
                if (inList.Contains(n.Record)) { parentOf[r] = n.Record; break; }
        if (parentOf.Count == 0) return items;
        var children = items.Where(i => parentOf.ContainsKey(i.Record)).GroupBy(i => parentOf[i.Record], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var result = new List<ListItem>();
        var placed = new HashSet<string>(StringComparer.Ordinal);
        void Add(ListItem it, int depth)
        {
            var indent = new string('　', depth * 2);
            if (children.TryGetValue(it.Record, out var kids) && placed.Add(it.Record))
            {
                var open = _openParents.Contains(it.Record);
                result.Add(it with { Display = $"{indent}{(open ? "▲" : "▼")} {it.Display}（{kids.Count} 件）", Depth = depth, ChildCount = kids.Count, IsOpen = open });
                if (open) foreach (var k in kids) Add(k, depth + 1);
            }
            else result.Add(depth == 0 ? it : it with { Display = indent + it.Display, Depth = depth });
        }
        foreach (var it in items) if (!parentOf.ContainsKey(it.Record)) Add(it, 0);
        return result;
    }

    // 親の行を開け閉めする (詳細は開き直さない。一覧のスクロールの位置は保つ)。選んでいる行はそのまま選ぶ。
    // 選んでいた行が閉じた親の中に隠れるときは、どの行も選ばない (親の行を選んだ形にすると、詳細が子のままなのに親を選んでいるように見えるため。詳細は子のまま)。
    // focus: 開け閉めの後、選んでいる行 (無ければ一覧) に焦点を戻す (←/→ で開け閉めしたとき)
    private void ToggleParent(string record, bool focus = false)
    {
        if (!_openParents.Remove(record)) _openParents.Add(record);
        var selected = (lstCandidates.SelectedItem as ListItem)?.Record;
        ShowRows(selected, reveal: false, keepScroll: true, focusSelected: focus);
    }

    // 親の行を押した (選んで詳細を出すのは SelectionChanged。押すたびに開け閉めする)
    private void LstCandidates_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject src) return;
        if (ItemsControl.ContainerFromElement(lstCandidates, src) is ListBoxItem { DataContext: ListItem { ChildCount: > 0 } row }) ToggleParent(row.Record);
    }

    // リストの行 (と下の仕切り) を、出す品目があるときだけ見せる (v1.22.3 の候補の一覧と同じく、空なら隠す)。
    // 隠すときは行の高さを Auto にして詰め、見せるときは隠す前の高さ (仕切りで変えた高さ。初めは 200) に戻す
    private GridLength _listHeight = new(200);
    private void ShowListArea(bool show)
    {
        var visible = lstCandidates.Visibility == Visibility.Visible;
        if (show == visible && (show || rowList.Height.IsAuto)) return;
        if (show)
        {
            rowList.MinHeight = 60;
            rowList.Height = _listHeight;
            lstCandidates.Visibility = Visibility.Visible;
            splitList.Visibility = Visibility.Visible;
        }
        else
        {
            if (!rowList.Height.IsAuto) _listHeight = rowList.Height;
            rowList.MinHeight = 0;
            rowList.Height = GridLength.Auto;
            lstCandidates.Visibility = Visibility.Collapsed;
            splitList.Visibility = Visibility.Collapsed;
        }
    }

    private void SelectInList(string record)
    {
        if (lstCandidates.ItemsSource is not List<ListItem>) return;
        // 閉じた親の行の中の行なら、親を開いて選ぶ (ShowRows)。スクロールの位置は保ち、選んだ行が見えるようにする
        ShowRows(record, keepScroll: true);
        if (lstCandidates.SelectedItem is { } sel) lstCandidates.ScrollIntoView(sel);
    }

    private void TxtQuery_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && lstCandidates.Items.Count > 0)
        {
            lstCandidates.SelectedIndex = 0;   // 選ぶと詳細を出す
            (lstCandidates.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (lstCandidates.Items.Count > 0) lstCandidates.SelectedIndex = 0;
            e.Handled = true;
        }
    }

    private void LstCandidates_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        // 選ぶ前の状態 (選んでいた行は外れた行) を覚えておき、詳細が変わるときに戻るの履歴に積む (Open)
        var before = CaptureState(selected: e.RemovedItems.Count > 0 ? e.RemovedItems[0] as ListItem : null, useSelectedOverride: true);
        if (lstCandidates.SelectedItem is ListItem item) Open(item, before);
    }

    private void LstCandidates_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && lstCandidates.SelectedItem is ListItem item) { Open(item, CaptureState()); e.Handled = true; }
        // 親の行: → で開く、← で閉じる
        else if (e.Key is Key.Right or Key.Left && lstCandidates.SelectedItem is ListItem { ChildCount: > 0 } parent && parent.IsOpen == (e.Key == Key.Left))
        {
            ToggleParent(parent.Record, focus: true);
            e.Handled = true;
        }
    }

    // リストの品目の詳細を出す (名前の欄は変えない。変えるとリストの絞り込みが変わるため)。record が無ければ名前で引き直す。
    // 出す詳細が今の詳細と違えば、before (選ぶ前の状態) を戻るの履歴に積む
    private async void Open(ListItem item, HistoryState before)
    {
        try
        {
            var record = item.Record;
            if (record.Length == 0)
            {
                var found = await SearchAllAsync(item.Name);
                record = found?.FirstOrDefault(c => c.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase))?.Record ?? found?.FirstOrDefault()?.Record ?? "";
                if (record.Length == 0) { PushHistory(before); _detailVersion++; _detailRecord = null; ShowDetail($"「{item.Name}」が見つかりませんでした。"); return; }
            }
            if (!string.Equals(record, _detailRecord, StringComparison.OrdinalIgnoreCase)) PushHistory(before);
            await LoadDetailAsync(record);
        }
        catch (Exception ex) { _detailRecord = null; ShowDetail($"詳細を出せませんでした: {ex.Message}"); txtStatus.Text = ""; }
    }

    // ===== 詳細 =====

    // 品目・資源の詳細、またはお店・場所・星系の詳細 (record が "Shop:" / "Place:" / "System:" で始まるもの。ShopTree.IsTreeRecord) を出す。
    // 品目の詳細の【販売場所】の店は、shop_price_links で店に結べた行だけお店の詳細へのリンクにする (ShopTree を作れなければリンク無し)
    private async Task LoadDetailAsync(string record)
    {
        // 詳細の表示 1 回分だけ、所持船を覚える (ShowItem が入れた頼みの品のときだけ。ほかの道 (一覧・検索・履歴・リンク) で出したら捨てる)
        _ownedShip = _ownedNext is { } on && string.Equals(on.Record, record, StringComparison.OrdinalIgnoreCase) ? on : null;
        _ownedNext = null;
        var version = ++_detailVersion;
        _detailRecord = record;
        var status = txtStatus.Text;
        if (status == LoadingText) status = "";   // 前の読み込みの「読み込み中...」は戻さない
        // 本来の表示は、ほかに進行中の読み込みが無ければ今の表示。進行中の読み込みがあれば、その読み込みの結果 (覚えてあるもの) のまま
        if (_loadingCount == 0) _statusAfterLoading = status;
        txtStatus.Text = LoadingText;
        var db = DbPath();
        string? text;
        string itemType = "";
        string? error = null;
        var isTreeRecord = ShopTree.IsTreeRecord(record);
        ShopTree? tree = null;
        HashSet<string>? mapShops = null;
        _loadingCount++;
        try
        {
            // お店・場所・星系 (その詳細と、販売場所のリンクに使う)。品目の詳細では、木を作れなくても詳細はリンク無しで出す
            try { tree = await GetShopTreeAsync(); }
            catch when (!isTreeRecord) { tree = null; }
            mapShops = tree != null ? MapShopRecords(tree) : null;
            (text, itemType) = await Task.Run(() =>
            {
                if (isTreeRecord) return (tree != null ? GameDataQueryService.ShopTreeDetailText(tree, record, mapShops) : null, "");
                if (!File.Exists(db)) return ((string?)null, "");
                using var q = GameDataQueryService.OpenReadOnly(db);
                return (q.GetItemDetailText(record, shopTree: tree, mapShops: mapShops), q.ItemTypeOf(record));
            });
        }
        catch (Exception ex) { text = null; error = $"読み込めませんでした: {ex.Message}"; }
        finally { _loadingCount--; }
        if (version == _detailVersion && error != null) ShowStatus(error);
        // 最後の読み込みなら「読み込み中...」を本来の表示に戻す。ほかの読み込み (分類・詳細) がまだ進行中なら、その「読み込み中...」を残す
        RestoreStatusIfIdle();
        if (version != _detailVersion) return;
        if (text == null)
        {
            ShowDetail(!File.Exists(db) ? "gamedata_cache.db がありません (設定タブの［インデックス構築］)"
                : isTreeRecord ? "お店・場所が見つかりませんでした。" : "品目が見つかりませんでした。");
            return;
        }
        ShowDetail(text);
        // 船・車両 (NOITEM_Vehicle) なら［3D 表示］［別ウィンドウ］を出す
        if (itemType.StartsWith("NOITEM_Vehicle", StringComparison.OrdinalIgnoreCase)) SetShip3DTarget(record, ShipNameFor(record));
        // 武器・装備・船の部品などの品 (EntityClassDefinition の品目) でも出す (2026-11-04、利用者の要望「武器の3D出ないけど？」)。形が無い品は押すと「形がありません」と出る
        else if (!isTreeRecord && itemType.Length > 0 && record.StartsWith("EntityClassDefinition.", StringComparison.OrdinalIgnoreCase))
        {
            SetShip3DTarget(record, ShipNameFor(record));
            // (2026-11-05、利用者の指示「右側にアイコン上に斜め 3D を出し、クリックすると出る」) 船でない品は、見出しの右に 3D の小さな絵を出す (船は今のまま)
            _ = ShowItemThumbAsync(record, ShipNameFor(record));
        }
        // ステーション・都市・前哨基地・小惑星 (星図の Manmade 系・LandingZone・Outpost 系・Asteroid 系。IsStationPlace) なら［読み込み］［地図表示］［別ウィンドウ］を出す
        if (isTreeRecord && tree != null && tree.NodesByRecord.TryGetValue(record, out var place) && IsStationPlace(place)) SetStationTarget(place, tree);
        // 店で、店のある場所 (かその上) が地図の一覧に出る場所 (StationMapPlaceOf) なら［マップに表示］を出す
        else if (isTreeRecord && tree != null && tree.ShopsByRecord.TryGetValue(record, out var shopHere) && StationMapPlaceOf(shopHere) is { } shopPlace)
            SetStationTarget(shopPlace, tree, shopHere);

        // 保存データに購入の売り場が無ければ、UEX の値段 (チャット・表と同じ取得・キャッシュ) を取ってから出し直す
        if (!text.Contains("店の販売データ無し")) return;
        var ids = GameDataQueryService.UexCategoryIdsForItemType(itemType);
        if (ids.Length == 0) return;
        try
        {
            var uex = await ChatService.GetSharedUexItemPricesAsync(ids);
            if (uex.Count == 0 || version != _detailVersion) return;
            var again = await Task.Run(() =>
            {
                using var q = GameDataQueryService.OpenReadOnly(db);
                return q.GetItemDetailText(record, uex, tree, mapShops: mapShops);
            });
            if (version != _detailVersion || again == null) return;
            ShowDetail(again);
        }
        catch { }   // UEX が取れなくても保存データの詳細のまま
    }

    // ===== 船の 3D 表示 =====

    // 3D のボタンを出している船の record (船・車両の詳細を出しているとき。出していなければ null) と名前
    private string? _ship3DRecord;
    private string _ship3DName = "";
    // 下の欄に 3D を出している船の短い名前 (出していなければ null)。出している間は容量の上限で消されないようにする (Ship3DExports.AcquireView)
    private string? _ship3DShown;
    // 3D の WebView2 (詳細の WebView2 とは別)。初期化は一度だけ
    private Task? _web3DInit;

    // 3D のボタンの船を決める (null なら隠し、3D 表示もやめる)
    private void SetShip3DTarget(string? record, string name)
    {
        if (!_ship3DHooked)
        {
            _ship3DHooked = true;
            // CPU 使用率が 90% をまたいだ・書き出しが加わった / 終わった・照合・キャッシュを書いた / 消した・置き場所が変わったら、ボタンを決め直す
            Ship3DExports.Instance.StateChanged += UpdateShip3DButtons;
            // 書き出し中の進み具合の文 (1 秒ごとに全体の文が変わるときに出し直す)
            Ship3DExports.Instance.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Ship3DExports.OverallText)) UpdateShip3DButtons(); };
        }
        if (!string.Equals(record, _ship3DRecord, StringComparison.OrdinalIgnoreCase)) _thumbError = null;
        _ship3DRecord = record;
        _ship3DName = name;
        if (record == null)
        {
            HideShip3D();
            pnlShip3D.Visibility = Visibility.Collapsed;
            return;
        }
        pnlShip3D.Visibility = Visibility.Visible;
        UpdateShip3DButtons();
        // パッチで Data.p4k が変わっていれば、この船の元データを照合する (終わったら StateChanged でボタンを決め直す)
        if (Ship3DExports.Instance.StateOf(record) == Ship3DCacheState.NeedsVerify) _ = Ship3DExports.Instance.VerifyAsync(record);
    }
    private bool _ship3DHooked;

    // ［読み込み］は、キャッシュが無い (照合で消したものを含む) ときで、書き出し中でなく、CPU 使用率が 90% 未満のときだけ使える。
    // 最新のキャッシュがあるとき・照合中は使えない。［3D 表示］［別ウィンドウ］はいつでも押せる (キャッシュが無ければ［読み込み］を押すよう知らせる)
    private void UpdateShip3DButtons()
    {
        if (_ship3DRecord == null) return;
        var ex = Ship3DExports.Instance;
        var state = ex.StateOf(_ship3DRecord);
        var shortName = Ship3DExports.ShortName(_ship3DRecord);
        var job = ex.Jobs.FirstOrDefault(j => j.Short.Equals(shortName, StringComparison.OrdinalIgnoreCase));
        // 自動の取り直しの順番待ちの間も押せる (押すとその船をいちばん先にする。Ship3DExports.IsRefreshWaiting・PromoteRefresh)
        // (2026-11-04) 「3D モデルを保存しない」のときは取り込み (保存) の［読み込み］は使わない (［3D 表示］で直接読む)
        var noShape = ex.NoShape(_ship3DRecord);
        btnShip3DImport.IsEnabled = !noShape && !Ship3DExports.NoSave && (state == Ship3DCacheState.None && job == null && !ex.CpuHigh || ex.IsRefreshWaiting(_ship3DRecord));
        txtShip3D.Text = noShape && state == Ship3DCacheState.None ? "3D モデル: この品には形がありません (形のデータが無い品です)"
            : job != null ? $"3D モデル: 書き出し中 {job.Text}"
            // 品の絵のための自動の取り込みの失敗 (知らせの窓は出さず、ここの文とログだけ。ShowItemThumbAsync)
            : state == Ship3DCacheState.None && _thumbError is { } te && string.Equals(te.Record, _ship3DRecord, StringComparison.OrdinalIgnoreCase) ? $"3D モデル: 取り込めませんでした: {te.Message}"
            : state switch
            {
                Ship3DCacheState.Fresh => "3D モデル: キャッシュあり",
                Ship3DCacheState.NeedsVerify or Ship3DCacheState.Verifying => "3D モデル: 照合中 (パッチで Data.p4k が変わったので元データを確かめています)",
                Ship3DCacheState.NoGame => "3D モデル: ゲームの Data.p4k が見つかりません",
                // 前の版で取り込んだ船: ［3D 表示］は取り込み済みのモデルをそのまま出す (Ship3DExports.OldShipCachePath)
                _ when ex.OldShipCachePath(_ship3DRecord) != null => Ship3DExports.NoSave ? "3D モデル: キャッシュあり (前の版で取り込んだもの)"
                    : "3D モデル: キャッシュあり (前の版で取り込んだもの。［読み込み］で取り込み直せます)",
                _ => Ship3DExports.NoSave ? "3D モデル: 保存しない設定 (［3D 表示］でゲームデータから直接読みます)"
                    : ex.CpuHigh ? $"3D モデル: キャッシュなし。CPU 使用率 {ex.CpuPercent:F0}% のため、下がってから読み込みます"
                                : "3D モデル: キャッシュなし (［3D 表示］でゲームデータから直接読みます)",
            };
    }

    // 船の名前 (リストで選んでいる行がその船ならその名前、無ければ record の短い名前)
    private string ShipNameFor(string record) =>
        lstCandidates.SelectedItem is ListItem item && item.Record.Equals(record, StringComparison.OrdinalIgnoreCase) && item.Name.Length > 0
            ? item.Name : Ship3DExports.ShortName(record);

    // 表示に使う GLB のパス (2026-11-04、利用者の決定「取り込み済みなら取り込みデータ、取り込みしてなければ直接」):
    // 取り込み済みならそれ (Data.p4k が変わっていれば照合してから)、無ければゲームデータから直接読む (確認の窓は出さない。進み具合は進み具合の欄に出る)。
    // 読めなければ null (知らせは Ship3DExports が出す)
    // quiet: 失敗の知らせの窓を出さない (品の絵のための自動の取り込み。ログと Ship3DExports.LastQuietError だけ)
    private async Task<string?> ViewPathAsync(string record, string name, bool quiet = false)
    {
        var ex = Ship3DExports.Instance;
        var state = ex.StateOf(record);
        if (state is Ship3DCacheState.NeedsVerify or Ship3DCacheState.Verifying) txtShip3D.Text = "3D モデル: 照合中 (パッチで Data.p4k が変わったので元データを確かめています)";
        var path = await ex.GetViewPathAsync(record, name, Window.GetWindow(this), quiet);
        UpdateShip3DButtons();
        return path;
    }

    // ［読み込み］: 予想を出して確かめてから裏で書き出す。終わったとき、まだその船の詳細を出していれば下の欄に 3D を出す (別の船に移っていたら知らせだけ)
    private async void BtnShip3DImport_Click(object sender, RoutedEventArgs e)
    {
        var record = _ship3DRecord;
        if (record == null) return;
        var name = _ship3DName;
        btnShip3DImport.IsEnabled = false;
        var path = await Ship3DExports.Instance.ImportAsync(record, name, Window.GetWindow(this));
        UpdateShip3DButtons();
        if (path == null) return;
        if (!string.Equals(record, _ship3DRecord, StringComparison.OrdinalIgnoreCase))
        {
            txtStatus.Text = $"「{name}」の 3D モデルを取り込みました (その船の詳細の［3D 表示］で出せます)";
            return;
        }
        await ShowShip3DAsync(record, name, path);
    }

    // ［3D 表示］: 下の欄に出す (取り込み済みならそれ、無ければゲームデータから直接読む)
    private async void BtnShip3D_Click(object sender, RoutedEventArgs e)
    {
        var record = _ship3DRecord;
        if (record == null) return;
        var name = _ship3DName;
        var path = await ViewPathAsync(record, name);
        if (path == null) return;
        if (!string.Equals(record, _ship3DRecord, StringComparison.OrdinalIgnoreCase)) return;
        await ShowShip3DAsync(record, name, path);
    }

    // ===== 船でない品の 3D の小さな絵 (2026-11-05、利用者の指示「R97 Shotgun（個人武器）の右側にアイコン上に斜め 3D を出し、クリックすると出るのが良いかも」) =====
    // 品の詳細を開いたとき、見出し (h2) の右に、その品の 3D を斜め (ビューアの船外の初めの向き) から描いた PNG (192×192。表示は高さ 96 px) を出す。
    // 絵を押すと［3D 表示］と同じく下の欄に 3D を出す (ページの chrome.webview.postMessage {type:'ship3d'} → OnDetailWebMessage)。
    //   絵の元の GLB: ［3D 表示］と同じ流れ (ViewPathAsync → GetViewPathAsync): キャッシュがあればそれ、無ければ取り込みを裏で自動で始める (確認の窓は出さない。
    //     進み具合は［読み込み］と同じく txtShip3D の「書き出し中」)。「3D モデルを保存しない」なら作業用のフォルダに直接読む。形が無い品は null (絵は出さない。文は「形がありません」)。
    //     ゲームの Data.p4k が見つからないとき (NoGame) は始めない (GetViewPathAsync が知らせの窓を出すため。文は txtShip3D に出ている)
    //   絵のファイル: GLB の横に同じ名前の .png (Ship3DExports.ThumbPath。保存しない設定では作業用のフォルダの一時の GLB の横。表示をやめたら GLB と一緒に消える)。
    //     .png が GLB より新しければそれを使い、無い・古ければ、隠した WebView2 (webThumb) でビューアを ?thumb=1 で開いて 1 コマ描き、PNG (data URL) を受け取って書く。
    //     保存しない設定で一時の GLB が消えた後も同じ品をまた開いたときに取り直さないよう、描いた絵はこの画面が生きている間は覚えておく (_thumbMemory)
    private readonly Dictionary<string, byte[]> _thumbMemory = new(StringComparer.OrdinalIgnoreCase);
    // 今の詳細に出す絵 (record と data URL)。詳細のページを出し直したとき (UEX の値段で作り直すとき) にもう一度差し込む
    private (string Record, string DataUrl)? _thumb;
    // 絵のための自動の取り込みの失敗 (record と文)。知らせの窓は出さず txtShip3D の文 (UpdateShip3DButtons) とログだけにする (2026-11-05、親の決定)。
    // 利用者が［読み込み］［3D 表示］を押したときの失敗は今までどおり窓を出す
    private (string Record, string Message)? _thumbError;
    private Task? _webThumbInit;
    private bool _webThumbHooked;
    private TaskCompletionSource<string?>? _thumbTcs;
    private readonly SemaphoreSlim _thumbGate = new(1, 1);
    private const int ThumbRenderTimeoutMs = 120_000;

    private async Task ShowItemThumbAsync(string record, string name)
    {
        var ex = Ship3DExports.Instance;
        // 詳細の WebView2 が使えない (文字の欄で出している) ときは絵を置く所が無いので描かない
        if (_webFailed || ex.StateOf(record) == Ship3DCacheState.NoGame) return;
        var shortName = Ship3DExports.ShortName(record);
        if (_thumbError is { } old && string.Equals(old.Record, record, StringComparison.OrdinalIgnoreCase)) _thumbError = null;
        byte[]? png = null;
        // 覚えた絵は色付けの設定ごとに分ける (保存しない設定の GLB は今の版で書かれるので、版では分けない)
        var memoryKey = App.Config.Ship3DPalette ? shortName : shortName + "|" + ShipPaints.IdNoPalette;
        if (_thumbMemory.TryGetValue(memoryKey, out var remembered) && ex.StateOf(record) == Ship3DCacheState.None && Ship3DExports.NoSave) png = remembered;
        if (png == null)
        {
            var path = await ViewPathAsync(record, name, quiet: true);
            if (path == null)
            {
                // 失敗の文 (quiet で窓を出さなかったもの) を txtShip3D に出す。形が無い品は LastQuietError が無く「形がありません」の文のまま
                if (Ship3DExports.LastQuietError is { } msg && string.Equals(record, _ship3DRecord, StringComparison.OrdinalIgnoreCase))
                {
                    _thumbError = (record, msg);
                    UpdateShip3DButtons();
                }
                return;
            }
            if (!string.Equals(record, _ship3DRecord, StringComparison.OrdinalIgnoreCase)) return;
            // 色付けを切っている・GLB が古い版の材質の決め方で書かれているときは、その材質の表を当てた絵を別の名前で持つ (ShipPaints.ThumbTableId・ThumbPath。2026-11-05、親の決定)。
            // どちらでもなければ今までどおり GLB の横の .png
            var tableId = await Task.Run(() => ShipPaints.ThumbTableId(path));
            var thumbPath = tableId == null ? Ship3DExports.ThumbPath(path) : ShipPaints.ThumbPath(path, shortName, tableId);
            try
            {
                if (File.Exists(thumbPath) && File.GetLastWriteTimeUtc(thumbPath) >= File.GetLastWriteTimeUtc(path)) png = await File.ReadAllBytesAsync(thumbPath);
            }
            catch (Exception e) { AppendAppLog($"[3D モデル] 絵 {Path.GetFileName(thumbPath)} を読めませんでした: {e.Message}"); }
            if (png == null)
            {
                // 描いている間はその GLB を「表示中」にして消されないようにする (保存しない設定の一時ファイルも)
                ex.AcquireView(shortName);
                string? table = null;
                try
                {
                    if (tableId != null)
                    {
                        // 表を作れなければ GLB の材質のまま描き、その絵は保存しない (次に開いたときにまた作る)
                        try { table = await ShipPaints.GetTableAsync(shortName, path, tableId); }
                        catch (Exception e) { AppendAppLog($"[3D モデル] 「{shortName}」の絵に当てる材質の表 ({tableId}) を作れませんでした (GLB の材質のまま描きます): {e.Message}"); }
                    }
                    png = await RenderThumbAsync(shortName, path, table);
                }
                finally { ex.ReleaseView(shortName); }
                if (png == null) return;
                if (tableId == null || table != null)
                    try { Directory.CreateDirectory(Path.GetDirectoryName(thumbPath)!); await File.WriteAllBytesAsync(thumbPath, png); }
                catch (Exception e) { AppendAppLog($"[3D モデル] 絵 {Path.GetFileName(thumbPath)} を書けませんでした: {e.Message}"); }
            }
            _thumbMemory[memoryKey] = png;
        }
        if (!string.Equals(record, _ship3DRecord, StringComparison.OrdinalIgnoreCase)) return;
        _thumb = (record, "data:image/png;base64," + Convert.ToBase64String(png));
        await InjectThumbAsync();
    }

    // 隠した WebView2 (webThumb) でビューアを ?thumb=1 で開き、描いた PNG を受け取る。描けなければ null (理由はログ)。1 つずつ描く
    // tablePath: 当てる材質の表 (無ければ GLB の材質のまま)
    private async Task<byte[]?> RenderThumbAsync(string shortName, string glbPath, string? tablePath = null)
    {
        await _thumbGate.WaitAsync();
        try
        {
            try { await (_webThumbInit ??= Ship3DWeb.InitAsync(webThumb)); }
            catch (Exception e)
            {
                _webThumbInit = null;
                AppendAppLog($"[3D モデル] 絵を描く WebView2 の初期化に失敗しました: {e.GetType().FullName} (HResult 0x{e.HResult:X8}): {e}");
                return null;
            }
            if (!_webThumbHooked)
            {
                _webThumbHooked = true;
                webThumb.CoreWebView2.WebMessageReceived += (_, e) =>
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                        var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
                        if (type == "thumb" && doc.RootElement.TryGetProperty("png", out var p) && p.ValueKind == JsonValueKind.String) _thumbTcs?.TrySetResult(p.GetString());
                        else if (type == "error") _thumbTcs?.TrySetResult(null);
                    }
                    catch (Exception ex) { AppendAppLog($"[3D モデル] 絵の知らせを扱えませんでした: {ex.Message}"); _thumbTcs?.TrySetResult(null); }
                };
            }
            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _thumbTcs = tcs;
            try
            {
                Ship3DWeb.ShowThumb(webThumb, shortName, glbPath, tablePath);
                var done = await Task.WhenAny(tcs.Task, Task.Delay(ThumbRenderTimeoutMs));
                if (done != tcs.Task) { AppendAppLog($"[3D モデル] 「{shortName}」の絵が {ThumbRenderTimeoutMs / 1000} 秒たっても描けなかったのでやめました"); return null; }
                var dataUrl = tcs.Task.Result;
                const string prefix = "data:image/png;base64,";
                if (dataUrl == null || !dataUrl.StartsWith(prefix, StringComparison.Ordinal)) return null;
                return Convert.FromBase64String(dataUrl[prefix.Length..]);
            }
            catch (Exception e)
            {
                AppendAppLog($"[3D モデル] 「{shortName}」の絵を描けませんでした: {e}");
                return null;
            }
            finally
            {
                _thumbTcs = null;
                Ship3DWeb.Blank(webThumb);   // 読み込んだ GLB の分のメモリを放す
            }
        }
        finally { _thumbGate.Release(); }
    }

    // 今の詳細のページの見出し (h2) の右に絵を差し込む (ページを作り直さない)。絵が今の詳細のものでなければ何もしない
    private async Task InjectThumbAsync()
    {
        if (_thumb is not { } th || !_webReady || !string.Equals(th.Record, _detailRecord, StringComparison.OrdinalIgnoreCase)) return;
        try { await webDetail.CoreWebView2.ExecuteScriptAsync(DetailHtml.ThumbScript(th.DataUrl)); }
        catch (Exception ex) { AppendAppLog($"[3D モデル] 絵を差し込めませんでした: {ex.Message}"); }
    }

    // 詳細のページからの知らせ: 絵を押した ({type:'ship3d'}) →［3D 表示］と同じ
    private void OnDetailWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "ship3d") BtnShip3D_Click(this, new RoutedEventArgs());
        }
        catch (Exception ex) { AppendAppLog($"[詳細] ページからの知らせを扱えませんでした: {ex.Message}"); }
    }

    // ［別ウィンドウ］: 別のウィンドウ (モードレス) に出す (読み込みは窓の側でする。取り込み済みならそれ、無ければゲームデータから直接読む)
    private void BtnShip3DWindow_Click(object sender, RoutedEventArgs e)
    {
        var record = _ship3DRecord;
        if (record == null) return;
        var name = _ship3DName;
        var owner = Window.GetWindow(this);
        try
        {
            var win = new Ship3DWindow(record, name, OwnedShipKeyFor(record));
            if (owner != null && owner.IsLoaded) win.Owner = owner;
            win.Show();
            // 別ウィンドウで出したら、この欄は詳細の表に戻す (利用者の要望。同じ 3D を 2 か所で出さない)。3D を出していなければ何もしない
            HideShip3D();
        }
        catch (Exception ex)
        {
            AppendAppLog($"[3D モデル] 別ウィンドウを開けませんでした: {ex}");
            MessageBox.Show($"別ウィンドウを開けませんでした: {ex.Message}", "3D 表示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ［表に戻る］
    private void BtnShip3DBack_Click(object sender, RoutedEventArgs e) => HideShip3D();

    // 下の欄に 3D を出す (詳細の表の WebView2 は隠すだけで、中身はそのまま)
    // ビューアの右上の閉じる (Ship3DWeb.CloseRequested): この欄の WebView2 なら［表に戻る］と同じ
    private bool _closeHooked;
    private void HookShip3DClose()
    {
        if (_closeHooked) return; _closeHooked = true;
        Ship3DWeb.CloseRequested += w => { if (ReferenceEquals(w, web3D)) Dispatcher.BeginInvoke(HideShip3D); };
    }
    private async Task ShowShip3DAsync(string record, string name, string? glbPath = null)
    {
        HookShip3DClose();
        try { await (_web3DInit ??= Ship3DWeb.InitAsync(web3D)); }
        catch (Exception ex)
        {
            _web3DInit = null;
            txtShip3D.Text = $"3D 表示 (WebView2) を使えません: {ex.Message}";
            AppendAppLog($"[3D モデル] WebView2 の初期化に失敗しました: {ex.GetType().FullName} (HResult 0x{ex.HResult:X8}): {ex}");
            return;
        }
        // 待っている間に別の品目に移っていたら出さない
        if (!string.Equals(record, _ship3DRecord, StringComparison.OrdinalIgnoreCase)) return;
        var shortName = Ship3DExports.ShortName(record);
        if (_ship3DShown != null) Ship3DExports.Instance.ReleaseView(_ship3DShown);
        Ship3DExports.Instance.AcquireView(shortName);
        _ship3DShown = shortName;
        try { Ship3DWeb.Show(web3D, shortName, name, glbPath, isItem: Ship3DExports.IsItemRecord(record), shipKey: OwnedShipKeyFor(record)); }
        catch (Exception ex)
        {
            AppendAppLog($"[3D モデル] 表示できませんでした: {ex}");
            txtShip3D.Text = $"3D 表示できませんでした: {ex.Message}";
            HideShip3D();
            return;
        }
        // (2026-11-04、利用者の指示「詳細の下に 3D 表示」) 詳細はそのまま残し、その下に 3D を出す
        web3D.Visibility = Visibility.Visible;
        split3D.Visibility = Visibility.Visible;
        Apply3DLayout();
        btnShip3DBack.Visibility = Visibility.Visible;
    }

    // 3D 表示をやめて詳細の表に戻す (3D のページは空にして、読み込んだ GLB の分のメモリを放す)
    private void HideShip3D()
    {
        if (_ship3DShown == null) return;
        Ship3DExports.Instance.ReleaseView(_ship3DShown);
        _ship3DShown = null;
        Ship3DWeb.Blank(web3D);
        web3D.Visibility = Visibility.Collapsed;
        // 仕切りを隠し、3D の行を元 (高さ Auto = 0) に戻す。詳細の文が今までどおり全体を使う
        split3D.Visibility = Visibility.Collapsed;
        row3D.MinHeight = 0;
        row3D.MaxHeight = double.PositiveInfinity;
        row3D.Height = GridLength.Auto;
        rowDetail.MinHeight = DetailMinHeight;
        btnShip3DBack.Visibility = Visibility.Collapsed;
    }

    // ===== 3D の欄の高さ (2026-11-05、利用者の指示「3D 部分を上に広げたりしたい」) =====
    // 詳細の文と 3D の欄の間の仕切り (split3D) を上下にドラッグして、3D の欄の高さを変える。船・車両の 3D でも武器・装備の 3D でも同じ欄 (web3D)。
    //   既定: Ship3DDefaultHeight (前は決まった高さ 340 だった)。
    //   下限: Ship3DMinHeight。ビューアの左上の名前 (上から 8 + 1 行)・右上の ✕ (上から 8 + 高さ 28)・左下の操作の説明
    //     (下から 8、12px × 1.5 の行。幅が狭いと 2 行が 4 行まで折り返す = 72 + 8) が重ならず欠けない高さ。
    //   上限: 詳細の文が DetailMinHeightWith3D 残る所まで (名前の見出しと小さな絵が読める高さ)。
    //   枠が「詳細の文の下限 + 仕切り + 3D の下限」より低いときは、2 つの下限を同じ割合で縮める (どちらも 0 にせず、枠の外へはみ出さない)。
    //   覚える: ドラッグを終えたときに App.Config.Detail3DHeight に入れて、すぐ設定を保存する (SaveConfig。MainWindow が渡す。［マップ］と同じ流儀)。
    //   割り当てられた高さが変わったとき (DetailArea_SizeChanged。窓の大きさ・上の一覧の仕切りなど): 覚えた高さを今の高さに収まるように当て直す (覚えた高さは変えない。広がれば覚えた高さに戻る)。
    //   3D の中身は、ビューア (wwwroot\ship3d\viewer.html) が window の resize で描く大きさと縦横の比を合わせ直す
    internal const double Ship3DDefaultHeight = 340;
    private const double Ship3DMinHeight = 120, DetailMinHeightWith3D = 100, DetailMinHeight = 40;

    // 設定を保存する (MainWindow.SaveConfigToFile を渡してもらう)
    public Action? SaveConfig { get; set; }

    // 覚えた高さ (設定の値が数でない・下限より小さければ既定)
    private static double Saved3DHeight() =>
        App.Config.Detail3DHeight is var h && double.IsFinite(h) && h >= Ship3DMinHeight ? h : Ship3DDefaultHeight;

    // 3D を出している間の行の高さと下限を決め直す (出していなければ何もしない)
    private void Apply3DLayout()
    {
        if (web3D.Visibility != Visibility.Visible) return;
        // 使える高さ: この行に割り当てられた高さ (空の部品 probeDetailArea の高さ) から枠の線を引いたもの
        var total = Math.Max(0, probeDetailArea.ActualHeight - borderDetail.BorderThickness.Top - borderDetail.BorderThickness.Bottom);
        var split = split3D.Height;
        double detailMin = DetailMinHeightWith3D, viewMin = Ship3DMinHeight;
        if (total > 0 && total < detailMin + split + viewMin)
        {
            // 枠が低い: 2 つの下限を同じ割合で縮める
            var room = Math.Max(0, total - split);
            detailMin = Math.Floor(room * DetailMinHeightWith3D / (DetailMinHeightWith3D + Ship3DMinHeight));
            viewMin = Math.Max(0, room - detailMin);
        }
        rowDetail.MinHeight = detailMin;
        row3D.MinHeight = viewMin;
        // 上限 (枠の高さ − 詳細の文の下限 − 仕切り)。仕切り (GridSplitter) は、片方の行が * のときは決まった高さの行 (3D の行) だけを変え、
        // その行の MinHeight / MaxHeight の中でしか動かさないので、上へ広げられる限りを 3D の行の MaxHeight で決める
        var viewMax = total > 0 ? Math.Max(viewMin, total - detailMin - split) : double.PositiveInfinity;
        row3D.MaxHeight = viewMax;
        row3D.Height = new GridLength(Math.Max(viewMin, Math.Min(Saved3DHeight(), viewMax)));
    }

    // 詳細の文と 3D の欄に割り当てられた高さが変わった (窓の大きさ・上の一覧の仕切り・上のバーの出し入れ)
    private void DetailArea_SizeChanged(object sender, SizeChangedEventArgs e) => Apply3DLayout();

    // 仕切りのドラッグを終えた: 今の 3D の欄の高さを覚えて、すぐ保存する
    private void Split3D_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        var h = row3D.ActualHeight;
        if (!double.IsFinite(h) || h <= 0) return;
        App.Config.Detail3DHeight = Math.Round(Math.Max(h, Ship3DMinHeight));
        try { SaveConfig?.Invoke(); }
        catch (Exception ex) { AppendAppLog($"[詳細] 3D の欄の高さを保存できませんでした: {ex.Message}"); }
        Apply3DLayout();
    }

    // ===== ステーションの地図 =====

    // バーを出している場所 (詳細の record (Place:…)・星図の場所・木。出していなければ null) と、下の欄に地図を出しているか
    private string? _stationDetailRecord;
    private ShopTreeNode? _stationPlace;
    private ShopTree? _stationTree;

    // バー (［読み込み］［地図表示］［別ウィンドウ］) を出す場所: 別ウィンドウの一覧と同じ種類 (ステーション・都市 (Manmade 系・LandingZone)・前哨基地 (Outpost 系)・小惑星 (Asteroid 系))。
    // 取り込みのルート探し (StationMapData の FindRoots) がこの種類 (StationPlace.IsMapPlace) を対象にしている
    private static bool IsStationPlace(ShopTreeNode n) => n.Type.StartsWith("Manmade", StringComparison.Ordinal) || n.Type == "LandingZone"
        || n.Type is "Outpost" or "Outpost_InvalidQT" or "Asteroid" or "Asteroid_ValidQT";

    // バーを出している店 (店の詳細のとき。［マップに表示］だけを出す。場所の詳細のときは null)
    private ShopTreeShop? _stationShop;

    // 店のある場所: 店の場所かその上で、最初の、3D (新しい方式) で開ける場所 (IsStationPlace の種類で、区画の置き方の一覧 (view3d_layout.db) にある場所)。無ければ null
    private static ShopTreeNode? StationMapPlaceOf(ShopTreeShop shop)
    {
        var inDb = AmenityMapHints.All();
        for (var n = shop.Location; n != null && !n.IsStar; n = n.Parent)
            if (IsStationPlace(n) && inDb.ContainsKey(n.RecordName)) return n;
        return null;
    }

    // バーの場所を決める (null なら隠す)。shop があれば店の詳細 (place は店のある場所。［3D 表示］は隠し、［マップに表示］だけを出す)
    private void SetStationTarget(ShopTreeNode? place, ShopTree? tree, ShopTreeShop? shop = null)
    {
        _stationDetailRecord = shop?.Record ?? place?.Record;
        _stationPlace = place;
        _stationTree = tree;
        _stationShop = place == null ? null : shop;
        btnStationMap.Visibility = _stationShop != null ? Visibility.Collapsed : Visibility.Visible;
        btnShopMap.Visibility = _stationShop != null ? Visibility.Visible : Visibility.Collapsed;
        if (place == null)
        {
            pnlStation.Visibility = Visibility.Collapsed;
            return;
        }
        if (!_stationHooked)
        {
            _stationHooked = true;
            // 区画の置き方の一覧を作り始めた・作り終えたら、文を決め直す
            StationView3DLayout.Changed += () => Dispatcher.BeginInvoke(UpdateStationButtons);
        }
        pnlStation.Visibility = Visibility.Visible;
        UpdateStationButtons();
    }
    private bool _stationHooked;

    // 横の文: 区画の置き方の一覧 (view3d_layout.db) の状態
    private void UpdateStationButtons()
    {
        if (_stationPlace == null) return;
        var inDb = AmenityMapHints.All().ContainsKey(_stationPlace.RecordName);
        txtStation.Text = StationView3DLayout.Running ? "区画の置き方の一覧を作っています (開くと、作り終えるまで待ちます)"
            : inDb ? "" : "この場所は区画の置き方の一覧にありません (設定タブの［マップをまとめて取り込む］で作り直せます)";
    }

    // ［3D 表示］: その場所の 3D (新しい方式) を出す (別ウィンドウが開いていればそこへ、無ければ［マップ］タブへ。StationView3DShops.Show)
    private void BtnStationMap_Click(object sender, RoutedEventArgs e)
    {
        if (_stationPlace is not { } place) return;
        StationView3DShops.Show(place.RecordName, place.Name, _stationTree, null, Window.GetWindow(this));
    }

    // 表の店名の右の「MAP」: その店のある場所 (StationMapPlaceOf) の 3D の窓を開き、その店を行き先にする (［マップに表示］と同じ)。
    // 店・場所が見つからなければ状態の欄に知らせる
    private async Task ShowShopOnMapAsync(string shopRecord)
    {
        var tree = await GetShopTreeOrNullAsync();
        if (tree != null && tree.ShopsByRecord.TryGetValue(shopRecord, out var shop) && StationMapPlaceOf(shop) is { } place)
        {
            StationView3DShops.Show(place.RecordName, place.Name, tree, shop.EntityName, Window.GetWindow(this));
            return;
        }
        ShowStatus("この店の場所の 3D が見つかりませんでした");
    }

    // 店名の右に「MAP」を付ける店: 店のある場所 (かその上) が 3D で開ける場所 (StationMapPlaceOf。店の詳細の［マップに表示］と同じ条件) の店の record
    private static HashSet<string> MapShopRecords(ShopTree tree) =>
        tree.Shops.Where(s => StationMapPlaceOf(s) != null).Select(s => s.Record).ToHashSet(StringComparer.Ordinal);

    // ［マップに表示］(店の詳細): 店のある場所の 3D の窓を開き、その店を行き先にする
    private void BtnShopMap_Click(object sender, RoutedEventArgs e)
    {
        if (_stationPlace is not { } place || _stationShop is not { } shop) return;
        StationView3DShops.Show(place.RecordName, place.Name, _stationTree, shop.EntityName, Window.GetWindow(this));
    }

}

// ［詳細］タブの詳細の文字 (GetItemDetailText の形) を Markdown にし、Markdig で HTML にする。
//   「【見出し】」の行 → 見出し (### )。見出しの後ろに文が続けば、その文は見出しの下の段落
//   「  項目: 値」の行の並び → 2 列の表 (項目 | 内容)
//   「  項目: A / B / C」の行の後に「A / B / C」と同じ数に分かれる行が続くもの (販売場所) → 項目の段落と、A / B / C を見出しにした表
//   「|」で始まる行 → そのまま Markdown の表 (距離による減衰・船の既定の装備。全角空白だけ文字参照にする)
//   ほかの行 → 段落 (続く行は改行でつなぐ)
//   文字はすべてミッションタブと同じ変換 (MissionDisplayText.Parse。<EM4> の中は強調の色、~mission(…) は〔〕) をしてから入れる
internal static class DetailHtml
{
    private static readonly Markdig.MarkdownPipeline Pipeline = Markdig.MarkdownExtensions.UsePipeTables(new Markdig.MarkdownPipelineBuilder()).Build();
    private static readonly Regex KeyValueRegex = new(@"^([^:：|]{1,30}?)[:：]\s+(.+)$");

    // 詳細の文字の中のリンク「[[record|表示名]]」(GameDataQueryService.DetailLink。record は URL エンコード済みで、使う文字は英数字と - _ . ~ %)。
    // 表示名の終わりは、後ろに "]" が続かない "]]" (表示名が "]" で終わるとき、例 "CSR-RP [軍1C]" でも切れないように)
    private static readonly Regex LinkRegex = new(@"\[\[([A-Za-z0-9%._~\-]+)\|(.*?)\]\](?!\])");
    private const string LinkScheme = "scjt://detail/";

    // 文字の欄 (WebView2 が使えないとき) 用: リンクを表示名だけにする
    public static string StripLinks(string text) => LinkRegex.Replace(text ?? "", m => m.Groups[2].Value);

    // 図の行「<<svg:BASE64>>」(GameDataQueryService.SvgLine)
    private static readonly Regex SvgLineRegex = new(@"^\s*<<svg:([A-Za-z0-9+/=]+)>>\s*$", RegexOptions.Multiline);

    // 場所の施設のアイコンの行「<<amen:BASE64>>」(GameDataQueryService.AmenityLine。中身は JSON の [{ "l": 施設の名前, "i": アイコンのパス }])
    private static readonly Regex AmenLineRegex = new(@"^\s*<<amen:([A-Za-z0-9+/=]+)>>\s*$", RegexOptions.Multiline);
    // 店名の右の「MAP」のリンク (GameDataQueryService.ShopMapLink。前の空白ごと)。文字の欄では押せないので消す
    private static readonly Regex ShopMapLinkRegex = new(@" ?\[\[" + Regex.Escape(Uri.EscapeDataString(GameDataQueryService.ShopMapPrefix)) + @"[A-Za-z0-9%._~\-]*\|MAP\]\](?!\])");

    // 施設の行の中身 (施設の名前とアイコンのパス)。読めなければ空
    private static List<(string Label, string Icon)> ParseAmenities(string base64)
    {
        var list = new List<(string, string)>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(base64)));
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return list;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                var l = e.TryGetProperty("l", out var lv) && lv.ValueKind == System.Text.Json.JsonValueKind.String ? lv.GetString() ?? "" : "";
                var i = e.TryGetProperty("i", out var iv) && iv.ValueKind == System.Text.Json.JsonValueKind.String ? iv.GetString() ?? "" : "";
                if (l.Length > 0) list.Add((l, i));
            }
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or ArgumentException) { }
        return list;
    }

    // 施設のアイコンの並び (HTML のブロック 1 行)。icons が true ならゲームのアイコン (濃い丸の背景に白の svg。マウスを乗せると施設の名前)、
    // false (アイコンを取り出せていない・データ API) なら施設の名前の文字だけ
    private static string AmenityBlock(List<(string Label, string Icon)> list, bool icons)
    {
        var sb = new StringBuilder("<div class=\"amenbar\">");
        foreach (var (label, icon) in list)
        {
            var name = System.Net.WebUtility.HtmlEncode(label);
            var url = AmenityIcons.Url(icon);
            if (icons && url != null)
                sb.Append($"<span class=\"amen\" title=\"{name}\"><img src=\"{System.Net.WebUtility.HtmlEncode(url)}\" alt=\"{name}\"></span>");
            else
                // 後からアイコンを割り当てたときに差し替えられるよう、アイコンの URL を data-icon に持たせる (SwapAmenityIconsScript)
                sb.Append($"<span class=\"amentext\"{(url != null ? $" data-icon=\"{System.Net.WebUtility.HtmlEncode(url)}\"" : "")}>{name}</span>");
        }
        return sb.Append("</div>").ToString();
    }

    // 施設の文字 (span.amentext[data-icon]) をアイコン (span.amen > img) に差し替えるスクリプト (［詳細］タブがアイコンを割り当てた後に、ページを作り直さずに走らせる)。
    // 名前は textContent・title で入れる (HTML として読まない)
    public const string SwapAmenityIconsScript = """
        (function () {
          Array.prototype.forEach.call(document.querySelectorAll('.amenbar span.amentext[data-icon]'), function (s) {
            var name = s.textContent, a = document.createElement('span'), img = document.createElement('img');
            a.className = 'amen'; a.title = name; img.src = s.getAttribute('data-icon'); img.alt = name;
            a.appendChild(img); s.parentNode.replaceChild(a, s);
          });
        })();
        """;

    // 船でない品の 3D の小さな絵 (PNG の data URL) を、見出し (最初の h2) の右に差し込むスクリプト (DetailPanel.InjectThumbAsync。ページを作り直さずに走らせる)。
    // 押すと chrome.webview.postMessage({type:'ship3d'}) でアプリに知らせる (DetailPanel.OnDetailWebMessage →［3D 表示］と同じ)。すでにあれば置き換える。
    // data URL は JSON の文字列にして入れる (' や \ を含まない英数字と +/=;:, だけだが、念のため)
    public static string ThumbScript(string dataUrl) => $$"""
        (function () {
          var h = document.querySelector('h2');
          if (!h) return;
          var old = document.getElementById('thumb3d');
          if (old) old.parentNode.removeChild(old);
          var img = document.createElement('img');
          img.id = 'thumb3d';
          img.src = {{System.Text.Json.JsonSerializer.Serialize(dataUrl)}};
          img.alt = '3D';
          img.title = '3D 表示 (押すと下の欄にこの品の 3D を出します)';
          img.addEventListener('click', function () { try { window.chrome.webview.postMessage({ type: 'ship3d' }); } catch (e) { } });
          h.appendChild(img);   // 見出しの文字のすぐ右 (2026-11-05、利用者「絵が遠すぎる。武器名のすぐ右がいい」)
        })();
        """;

    // 文字の欄 (WebView2 が使えないとき) に出す文字: リンクは表示名だけ (店名の右の「MAP」は消す)、図の行は「（図は WebView2 が必要です）」、
    // 施設のアイコンの行は「施設: A、B、…」
    public static string ForTextBox(string text)
    {
        var t = AmenLineRegex.Replace(ShopMapLinkRegex.Replace(text ?? "", ""), m => "施設: " + string.Join("、", ParseAmenities(m.Groups[1].Value).Select(a => a.Label)));
        return SvgLineRegex.Replace(StripLinks(t), "（図は WebView2 が必要です）");
    }

    // WebView2 の移動先がリンク (scjt://detail/<URL エンコードした record>) なら record を返す
    public static bool TryParseLink(string? uri, out string record)
    {
        record = "";
        if (uri == null || !uri.StartsWith(LinkScheme, StringComparison.OrdinalIgnoreCase)) return false;
        try { record = Uri.UnescapeDataString(uri[LinkScheme.Length..].TrimEnd('/')); }
        catch { return false; }
        return record.Length > 0;
    }

    // リンク 1 つを HTML の <a> にする (表示名は Markdown の記号を打ち消して入れる。href は URL エンコード済みの record なので、そのまま入れてよい)
    private static string LinkHtml(string encodedRecord, string displayMarkdown) =>
        $"<a href=\"{LinkScheme}{encodedRecord}\">{displayMarkdown}</a>";

    // 「|」で始まる行 (元から Markdown の表) の中のリンクを <a> にする。
    // リンクの外の文字は、生の HTML として読まれないよう < > & を文字参照にする (TableHtmlText)。リンクの表示名は今までどおり Escape で打ち消す
    private static string TableLineLinks(string line)
    {
        var sb = new StringBuilder(line.Length + 16);
        int pos = 0;
        foreach (Match m in LinkRegex.Matches(line))
        {
            if (m.Index > pos) sb.Append(TableHtmlText(line[pos..m.Index]));
            sb.Append(LinkHtml(m.Groups[1].Value, Escape(m.Groups[2].Value)));
            pos = m.Index + m.Length;
        }
        if (pos < line.Length) sb.Append(TableHtmlText(line[pos..]));
        return sb.ToString();
    }

    // 表の行の (リンクの外の) 文字の < > & を文字参照 (&lt; &gt; &amp;) にする。Markdig を通しても HTML のタグ・文字参照にならず、文字のまま出る。
    // 表を作る側が「\」で打ち消したもの (MdEscape の \< \>) はそのまま (前の「\」が奇数個なら打ち消し済み)。
    // 全角空白の文字参照 &#12288; (ToMarkdown が入れるもの) はそのまま
    private static string TableHtmlText(string s)
    {
        const string fullWidthSpace = "&#12288;";
        var sb = new StringBuilder(s.Length + 8);
        int backslashes = 0;
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            var escaped = backslashes % 2 == 1;
            backslashes = c == '\\' ? backslashes + 1 : 0;
            if (escaped) { sb.Append(c); continue; }
            switch (c)
            {
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '&' when string.CompareOrdinal(s, i, fullWidthSpace, 0, fullWidthSpace.Length) == 0:
                    sb.Append(fullWidthSpace);
                    i += fullWidthSpace.Length - 1;
                    break;
                case '&': sb.Append("&amp;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    // amenityIcons: 施設の行をゲームのアイコンで出す (［詳細］タブで、アイコンを WebView2 に割り当てたときだけ。AmenityIcons)。false なら施設の名前の文字
    public static string FromDetailText(string text, bool amenityIcons = false) => Page(Markdig.Markdown.ToHtml(ToMarkdown(text ?? "", amenityIcons), Pipeline));

    // 図 1 つの HTML のブロック (1 行): <div class="map"> の中に svg (GameDataQueryService.SystemMapSvg / NearMapSvg) と、右上に重ねる［全体］［＋］［－］の HTML のボタン。
    // ボタンは CSS (.mapctl) で枠の右上に置く (svg の中の座標には頼らない)。押したときの動きは MapScript。
    // 左下に凡例 (GameDataQueryService.MapLegendHtml。CSS の .maplegend。拡大・移動では動かない)。詳細タブとマップタブ (MapPanel) で使う。
    // withMapLegend: 凡例に地図のある場所の枠も入れる (マップタブだけ)
    public static string MapBlock(string svg, bool withMapLegend = false) =>
        "<div class=\"map\">" + svg.Replace("\r", "").Replace("\n", " ") +
        "<div class=\"mapctl\"><button type=\"button\" data-act=\"fit\">全体</button><button type=\"button\" data-act=\"in\">＋</button><button type=\"button\" data-act=\"out\">－</button></div>" +
        GameDataQueryService.MapLegendHtml(withMapLegend) + "</div>";

    public static string ToMarkdown(string text, bool amenityIcons = false)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).ToList();
        var md = new StringBuilder();
        var kv = new List<(string Key, string Value)>();
        var para = new List<string>();
        bool inTable = false;

        void FlushKv()
        {
            if (kv.Count == 0) return;
            md.AppendLine("| 項目 | 内容 |").AppendLine("|---|---|");
            foreach (var (k, v) in kv) md.AppendLine($"| {Inline(k)} | {Inline(v)} |");
            md.AppendLine();
            kv.Clear();
        }
        void FlushPara()
        {
            if (para.Count == 0) return;
            md.AppendLine(string.Join("<br>\n", para.Select(Inline))).AppendLine();
            para.Clear();
        }
        void FlushTable()
        {
            if (!inTable) return;
            md.AppendLine();
            inTable = false;
        }
        void FlushAll() { FlushKv(); FlushPara(); FlushTable(); }

        for (int i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (t.Length == 0) { FlushAll(); continue; }
            // 図の行「<<svg:BASE64>>」(GameDataQueryService.SvgLine) → インラインの SVG (div で囲んだ HTML のブロック。中は自分で作った SVG だけ)
            if (SvgLineRegex.Match(t) is { Success: true } svgMatch)
            {
                FlushAll();
                string svg;
                try { svg = Encoding.UTF8.GetString(Convert.FromBase64String(svgMatch.Groups[1].Value)); }
                catch (FormatException) { continue; }
                md.AppendLine(MapBlock(svg)).AppendLine();
                continue;
            }
            // 施設のアイコンの行「<<amen:BASE64>>」(GameDataQueryService.AmenityLine) → アイコン (か名前) の並び (HTML のブロック。名前は打ち消して入れる)
            if (AmenLineRegex.Match(t) is { Success: true } amenMatch)
            {
                FlushAll();
                var amen = ParseAmenities(amenMatch.Groups[1].Value);
                if (amen.Count > 0) md.AppendLine(AmenityBlock(amen, amenityIcons)).AppendLine();
                continue;
            }
            // 「【【名前（種類）】】」(GameDataQueryService.DetailTitle) → 大きめの見出し (## )
            if (t.StartsWith("【【", StringComparison.Ordinal) && t.EndsWith("】】", StringComparison.Ordinal) && t.Length > 4)
            {
                FlushAll();
                md.AppendLine("## " + Inline(t[2..^2])).AppendLine();
                continue;
            }
            if (t.StartsWith('【') && t.IndexOf('】') is var close && close > 0)
            {
                FlushAll();
                md.AppendLine("### " + Inline(t[1..close])).AppendLine();
                var rest = t[(close + 1)..].Trim();
                if (rest.Length > 0) para.Add(rest);
                continue;
            }
            if (t.StartsWith('|'))
            {
                FlushKv(); FlushPara();
                // 元から Markdown の表 (中身は数値と単位、船の既定の装備)。全角空白は文字参照にする
                // (Markdig は表のセルの前後の全角空白を消すので、既定の装備の入れ子の字下げ「　└」が消えないように。見た目は同じ全角空白)
                md.AppendLine(TableLineLinks(t.Replace("\u3000", "&#12288;")));
                inTable = true;
                continue;
            }
            FlushTable();
            var m = KeyValueRegex.Match(t);
            if (m.Success && !t.StartsWith('※'))
            {
                var key = m.Groups[1].Value.Trim();
                var value = m.Groups[2].Value.Trim();
                // 「項目: A / B / C」の後に同じ数に分かれる行が続けば、A / B / C を見出しにした表にする (販売場所)
                var head = SplitSlash(value);
                if (head.Count >= 3 && i + 1 < lines.Count && IsSlashRow(lines[i + 1].Trim(), head.Count))
                {
                    FlushKv(); FlushPara();
                    md.AppendLine("**" + Inline(key) + "**").AppendLine();
                    md.AppendLine("| " + string.Join(" | ", head.Select(Inline)) + " |");
                    md.AppendLine("|" + string.Concat(Enumerable.Repeat("---|", head.Count)));
                    while (i + 1 < lines.Count && IsSlashRow(lines[i + 1].Trim(), head.Count))
                    {
                        i++;
                        md.AppendLine("| " + string.Join(" | ", SplitSlash(lines[i].Trim()).Select(Inline)) + " |");
                    }
                    md.AppendLine();
                    continue;
                }
                FlushPara();
                kv.Add((key, value));
                continue;
            }
            FlushKv();
            para.Add(t);
        }
        FlushAll();
        return md.ToString();
    }

    private static List<string> SplitSlash(string s) => s.Split(" / ").Select(x => x.Trim()).ToList();

    // 「A / B / C」の形で count 個に分かれ、「項目: 値」の行でないもの
    private static bool IsSlashRow(string t, int count) =>
        t.Length > 0 && !t.StartsWith('|') && !t.StartsWith('【') && !KeyValueRegex.IsMatch(t) && SplitSlash(t).Count == count;

    // 文字を Markdown に入れる: ミッションタブと同じ変換をし、強調は色の付いた span にする。記号は \ で打ち消す (Markdown の書式にしない)。
    // リンク「[[record|表示名]]」は <a> にする (表示名にも同じ変換をする)
    private static string Inline(string s)
    {
        if (LinkRegex.IsMatch(s))
        {
            var sbl = new StringBuilder();
            int pos = 0;
            foreach (Match m in LinkRegex.Matches(s))
            {
                if (m.Index > pos) sbl.Append(InlineText(s[pos..m.Index]));
                sbl.Append(LinkHtml(m.Groups[1].Value, InlineText(m.Groups[2].Value)));
                pos = m.Index + m.Length;
            }
            if (pos < s.Length) sbl.Append(InlineText(s[pos..]));
            return sbl.ToString();
        }
        return InlineText(s);
    }

    private static string InlineText(string s)
    {
        var sb = new StringBuilder();
        foreach (var (chunk, emphasis) in MissionDisplayText.Parse(s))
        {
            var esc = Escape(chunk);
            sb.Append(emphasis ? $"<span class=\"em\">{esc}</span>" : esc);
        }
        return sb.ToString();
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            if (c < 128 && (char.IsPunctuation(c) || char.IsSymbol(c))) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    // 色は今のアプリ (明るい背景) に合わせる。表に罫線を付ける。強調 (<EM4>) は明るい背景で読める青
    // body は自分で作った HTML (名前などは打ち消し済み)。extraCss はページに足す CSS (マップタブの並べ方など)。スクリプトは MapScript だけ
    public static string Page(string body, string extraCss = "") => $$"""
        <!DOCTYPE html><html><head><meta charset="utf-8">
        <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; img-src https://{{AmenityIcons.Host}} data:">
        <style>
        body { font-family: 'Yu Gothic UI','Meiryo','Segoe UI',sans-serif; font-size: 13px; color: #212121; background: #FFFFFF;
               margin: 8px 10px; line-height: 1.5; word-wrap: break-word; }
        h2 { font-size: 1.35em; margin: 0.2em 0 0.5em; }
        .map { margin: 0.3em 0 0.8em; position: relative; }
        .map svg.zmap { display: block; width: 100%; height: 500px; cursor: grab; touch-action: none; user-select: none; -webkit-user-select: none; }
        .map svg.zmap.dragging { cursor: grabbing; }
        .map g.icon, .map g.icon.mark { fill: none; stroke: #6D4C41; stroke-width: 1.6; }
        .map g.icon.hl { fill: none; stroke: #D84315; }
        .map g.icon .solid { fill: #6D4C41; stroke: none; }
        .map g.icon.hl .solid { fill: #D84315; }
        .map g.icon * { pointer-events: visible; }
        .maplegend { position: absolute; left: 8px; bottom: 8px; display: flex; flex-wrap: wrap; gap: 2px 10px; padding: 2px 6px;
                     background: rgba(255,255,255,0.85); border: 1px solid #CCCCCC; border-radius: 3px; font-size: 22px; color: #424242; pointer-events: none; }
        .maplegend .lg { display: inline-flex; align-items: center; gap: 3px; }
        .maplegend svg { display: block; }
        .mapctl { position: absolute; top: 8px; right: 8px; display: flex; gap: 4px; }
        .mapctl button { font: inherit; font-size: 24px; min-width: 44px; height: 44px; padding: 0 12px; background: #FFFFFF; color: #212121;
                         border: 1px solid #90A4AE; border-radius: 3px; cursor: pointer; }
        .mapctl button:hover { background: #E3F2FD; }
        .map .bg { fill: #F7F9FC; stroke: #CCCCCC; }
        .map .orbit { fill: none; stroke: #B0BEC5; stroke-width: 1; }
        .map .star { fill: #F9A825; }
        .map .planet { fill: #1565C0; }
        .map .mark { fill: #6D4C41; }
        .map .hl { fill: #D84315; }
        .map .self { fill: #BBDEFB; stroke: #1565C0; stroke-width: 1; }
        .map text { fill: #212121; font-size: 22px; }
        .map text.small { font-size: 18px; fill: #5D4037; }
        .map g.cur { fill: none; stroke: #E53935; stroke-width: 2; }
        .map g.curmark.taken { cursor: pointer; }
        .map g.curmark.taken g.cur * { pointer-events: visible; }
        .map g.est circle { fill: #FFFFFF; stroke: #E53935; stroke-width: 3; }
        .map line.qt, .map polyline.qt { stroke: #E53935; stroke-width: 3; fill: none; }
        .map polyline.qt { stroke-dasharray: 10 6; }
        .map g.qtfrom circle { fill: #E53935; }
        .map g.obj, .map g.qtgoal { fill: #EF6C00; stroke: #EF6C00; stroke-width: 1.6; }
        .map g.qtgoal { stroke: #E53935; fill: #EF6C00; stroke-width: 2; }
        .map text.objtext { fill: #E65100; }
        .map circle.qtdest { stroke: #E53935; stroke-width: 3; }
        .map g.icon.qtdest { stroke: #E53935; stroke-width: 3; }
        .map .qthead { fill: #E53935; }
        .map g.qtend circle { fill: #E53935; }
        .map .here { fill: #E53935; }
        .map circle.area { fill: #6D4C41; fill-opacity: 0.12; stroke: none; pointer-events: none; }
        .map circle.bigbody { fill-opacity: 0.08; stroke: #1565C0; stroke-width: 1.5; }
        .map circle.bigbody.here, .map circle.bigbody.hl { stroke: #D84315; }
        .map circle.area.here { fill: #E53935; fill-opacity: 0.18; }
        .map g.icon.here { fill: none; stroke: #E53935; stroke-width: 2.4; }
        .map g.icon.here .solid { fill: #E53935; stroke: none; }
        .map text.curtext { fill: #E53935; font-weight: 600; }
        .map text.hltext { fill: #D84315; font-weight: 600; }
        h3 { font-size: 1.05em; margin: 1em 0 0.4em; padding-bottom: 2px; border-bottom: 1px solid #DDD; }
        h3:first-child { margin-top: 0.2em; }
        p { margin: 0.3em 0; }
        table { border-collapse: collapse; margin: 0.3em 0 0.6em; }
        th, td { border: 1px solid #BBB; padding: 3px 8px; vertical-align: top; text-align: left; }
        th { background: #E8E8E8; font-weight: 600; }
        tr:nth-child(even) td { background: #F8F8F8; }
        tr.fp { cursor: pointer; }
        table.foldtbl tr td { background: none; }
        table.foldtbl tr.ev td { background: #F8F8F8; }
        table.foldtbl tr.fp:hover td { background: #E3F2FD; }
        .em { color: #1565C0; }
        .amenbar { display: flex; flex-wrap: wrap; gap: 4px; margin: 0.2em 0 0.4em; }
        .amenbar .amen { display: inline-flex; align-items: center; justify-content: center; width: 30px; height: 30px; border-radius: 50%; background: #37474F; }
        .amenbar .amen img { width: 22px; height: 22px; display: block; }
        .amenbar .amentext { display: inline-block; padding: 1px 8px; border-radius: 3px; background: #37474F; color: #FFFFFF; }
        a[href^="scjt://detail/ShopMap%3A"] { display: inline-block; margin-left: 4px; padding: 0 5px; font-size: 0.85em; line-height: 1.4; text-decoration: none;
                                               border: 1px solid #90A4AE; border-radius: 3px; background: #FFFFFF; }
        a[href^="scjt://detail/ShopMap%3A"]:hover { background: #E3F2FD; }
        .map g.amenrow circle { fill: #37474F; stroke: none; }
        .map g.amenrow { pointer-events: visible; }
        #thumb3d { display: inline-block; vertical-align: middle; height: 96px; margin: 0 0 0 16px; cursor: pointer; }
        {{extraCss}}
        </style></head><body>{{body}}<script>{{MapScript}}</script></body></html>
        """;

    // 図 (GameDataQueryService.SystemMapSvg / NearMapSvg の svg.zmap) を動かすスクリプト (ページに 1 つ)。外への通信・ファイルの読み込みはしない。
    // ゲームデータの名前はここに入れない (点の座標などは svg の要素の data-* 属性から読む)。
    // ・表示: 中心 (ゲームの座標) と倍率 (px / メートル) から、data-x / data-y を持つ要素を置き直す。viewBox を枠の実際の大きさ (px) にするので、字と点の大きさは変わらない
    // ・倍率の範囲と最初の表示: GameDataQueryService.InitialMapView と同じ式
    // ・ホイール: カーソルの位置を中心に拡大・縮小。左ドラッグ: 移動 (動かした後のクリックではリンクを開かない)。右上の［全体］［＋］［－］(MapBlock の .mapctl)
    // ・枠の大きさが変わったら置き直す (ResizeObserver と窓の resize)
    // ・svg に data-save があれば (マップタブの図だけ)、操作 (ホイール・ドラッグ・ボタン) が止まって 500ms 後に
    //   window.chrome.webview.postMessage({ map: data-save の値, cx, cy, w }) で今の中心と幅 (メートル) をアプリへ送る (MapPanel が受けて覚える)
    // ・表示を変えるたびに、ラベルを data-pri の小さい順 (同じなら data-sub の多い順、同じなら data-no の順) に置き、先に置いたものと重なるものは隠す (点と <title> は残す)
    // ・マップタブの図 (data-save) だけ: 拡大の上限でさらに拡大したとき (ホイール・［＋］)、操作の位置 (ホイールはカーソル、［＋］は図の真ん中) から
    //   ENTER_PX 以内に地図のある場所の印 (a[data-map]) があれば、いちばん近いものを postMessage({ type: 'enterplace', rec }) でアプリへ知らせる
    //   (MapPanel がその場所の階層マップへ移る)。アプリからの { type: 'mapfocus', save, x, y } で、その位置を真ん中に拡大の上限で出す (地図から戻るとき・検索)
    private const string MapScript = """
        (function () {
          'use strict';
          // スクリプトのエラーをアプリへ知らせる ({ type: 'scripterror', where, message, stack }。［マップ］タブ (MapPanel) がアプリのログに書く)
          function reportError(where, e) {
            try {
              if (!(window.chrome && window.chrome.webview)) return;
              var message = (e && e.message !== undefined) ? String(e.message) : String(e);
              window.chrome.webview.postMessage({ type: 'scripterror', where: where, message: message, stack: (e && e.stack) ? String(e.stack) : '' });
            } catch (x) { }
          }
          window.addEventListener('error', function (ev) { reportError('event', ev.error || ev.message); });
          window.addEventListener('unhandledrejection', function (ev) { reportError('promise', ev.reason); });
          // (2026-11-05、利用者の指示「マップの現在位置をクリックしたときに建物内なら 3D 表示」) 取った座標の現在位置の印・その文字 (g.curmark.taken) と、
          // 【現在位置の近く】の現在地の文 (aside.near p.here) を押したら、アプリへ { type: 'gohere' } (［マップ］タブ (MapPanel.GoHere3DAsync) が、建物の中ならその場所の 3D を出す)。
          // ドラッグ (押した所から 6px より動いた) の後の click は見ない
          var downX = 0, downY = 0;
          document.addEventListener('pointerdown', function (ev) { downX = ev.clientX; downY = ev.clientY; }, true);
          document.addEventListener('click', function (ev) {
            if (Math.abs(ev.clientX - downX) + Math.abs(ev.clientY - downY) > 6) return;
            var t = ev.target;
            if (!(t && t.closest && t.closest('g.curmark.taken, aside.near p.here'))) return;
            if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage({ type: 'gohere' });
          });
          var FIT = 260 / 300, MARGIN = 1;
          // 拡大の上限でさらに拡大したときに、地図へ移る場所を探す範囲 (画面の px)。印のアイコン (外径 12px) と枠 (20px) の外に指 1 本ぶんの余裕を見て 40px
          // (仮の値。2026-11-01、利用者の決めた案 1 で親と決めた。動かしてみて直す)。同じ知らせを続けて送らないための間 (ms)
          var ENTER_PX = 40, ENTER_GAP_MS = 1500;
          function num(el, name, def) { var v = parseFloat(el.getAttribute(name)); return isNaN(v) ? def : v; }
          function setup(svg) {
            var st = { W: 600, H: 500, cx: 0, cy: 0, s: 1, min: 1, max: 1, ready: false };
            var fcx = num(svg, 'data-fcx', 0), fcy = num(svg, 'data-fcy', 0), fr = num(svg, 'data-fr', 1);
            var icx = num(svg, 'data-icx', fcx), icy = num(svg, 'data-icy', fcy), iw = num(svg, 'data-iw', 0), dmin = num(svg, 'data-dmin', 0);
            if (!(fr > 0)) return;
            var bg = svg.querySelector('.bg');
            var ctl = svg.parentNode ? svg.parentNode.querySelector('.mapctl') : null;
            // data-save があれば (マップタブの図だけ)、操作が止まって 500ms 後に今の表示 (中心と幅。メートル) をアプリへ送る
            var saveKey = svg.getAttribute('data-save'), saveTimer = 0;
            // data-x を持つ要素: circle (点・円・軌道の輪)、g.icon (印のアイコン。transform で置く。data-pr = 点の大きさ (半径 px))、text (ラベル)
            var items = Array.prototype.map.call(svg.querySelectorAll('[data-x]'), function (el) {
              var tag = el.tagName.toLowerCase();
              var it = {
                el: el, kind: tag === 'circle' ? 'circle' : tag === 'g' ? 'icon' : 'text',
                x: num(el, 'data-x', 0), y: num(el, 'data-y', 0), ox: num(el, 'data-ox', 0), oy: num(el, 'data-oy', 0),
                wr: el.hasAttribute('data-wr') ? num(el, 'data-wr', 0) : null, minr: num(el, 'data-minr', 0), hb: num(el, 'data-hidebelow', 0),
                r0: tag === 'circle' ? num(el, 'r', 0) : num(el, 'data-pr', 0), sx: 0, sy: 0, r: 0, tx: 0, ty: 0
              };
              el._zm = it;
              return it;
            });
            // 量子ジャンプの矢印の線 (data-x1 / data-y1 → data-x2 / data-y2。両端をゲームの座標で持つ)
            var lines = Array.prototype.map.call(svg.querySelectorAll('line[data-x1], polyline[data-x1]'), function (el) {
              return { el: el, poly: el.tagName.toLowerCase() === 'polyline', x1: num(el, 'data-x1', 0), y1: num(el, 'data-y1', 0), x2: num(el, 'data-x2', 0), y2: num(el, 'data-y2', 0) };
            });
            // ラベルと、その点 (同じ親 (<a> / <g>) の中の circle か g.icon)
            var labels = Array.prototype.slice.call(svg.querySelectorAll('text[data-pri]')).map(function (el, i) {
              var pt = null;
              Array.prototype.forEach.call(el.parentNode.children, function (c) {
                if (!pt && c !== el && c._zm && c._zm.kind !== 'text') pt = c._zm;
              });
              return { el: el, it: el._zm, pt: pt, pri: num(el, 'data-pri', 9), sub: num(el, 'data-sub', 0), no: num(el, 'data-no', 0), i: i };
            });
            // 置く順: data-pri の小さい順、同じなら子の場所の数 (data-sub) の多い順、同じなら名前の順 (data-no)
            labels.sort(function (a, b) { return a.pri - b.pri || b.sub - a.sub || a.no - b.no || a.i - b.i; });
            function fitScale() { return Math.min(st.W, st.H) / 2 * FIT / fr; }
            function limits() {
              var f = fitScale();
              st.min = f / 2;
              st.max = dmin > 0 ? Math.max(f, Math.min(st.W, st.H) / (2 * dmin)) : f;
            }
            function clamp(s) { return Math.max(st.min, Math.min(st.max, s)); }
            function fit() { st.cx = fcx; st.cy = fcy; st.s = clamp(fitScale()); }
            function initial() {
              if (iw > 0) { st.cx = icx; st.cy = icy; st.s = clamp(st.W / iw); } else { fit(); }
            }
            function notify() {
              if (saveKey === null || !(window.chrome && window.chrome.webview)) return;
              if (saveTimer) clearTimeout(saveTimer);
              saveTimer = setTimeout(function () {
                saveTimer = 0;
                window.chrome.webview.postMessage({ map: saveKey, cx: st.cx, cy: st.cy, w: st.W / st.s });
              }, 500);
            }
            // 枠の実際の大きさ (CSS の px) を読み、viewBox・背景を合わせる。枠が見えていない (幅 0) ときは false
            function measure() {
              var r = svg.getBoundingClientRect();
              if (r.width < 1 || r.height < 1) return false;
              st.W = r.width; st.H = r.height;
              svg.setAttribute('viewBox', '0 0 ' + st.W + ' ' + st.H);
              if (bg) { bg.setAttribute('width', Math.max(0, st.W - 1)); bg.setAttribute('height', Math.max(0, st.H - 1)); }
              limits();
              return true;
            }
            function render() {
              var s = st.s, hw = st.W / 2, hh = st.H / 2;
              items.forEach(function (it) {
                var sx = hw + (it.x - st.cx) * s, sy = hh - (it.y - st.cy) * s;
                var rr = it.wr === null ? null : Math.max(it.minr, it.wr * s);
                it.sx = sx; it.sy = sy;
                if (it.kind === 'circle') {
                  it.el.setAttribute('cx', sx); it.el.setAttribute('cy', sy);
                  if (rr !== null) it.el.setAttribute('r', rr);
                  // 惑星・衛星の円 (data-minr のある円) が画面で大きい (半径 150px 超) ときは、塗りを薄くして輪郭を出す (中の場所・アイコン・ラベルが見えるように)
                  if (rr !== null && it.el.hasAttribute('data-minr')) it.el.classList.toggle('bigbody', rr > 150);
                  // ステーションなどの大きさの円 (data-hidebelow): 画面の半径がそれ未満なら隠す
                  if (it.hb > 0) { if (rr !== null && rr < it.hb) it.el.setAttribute('visibility', 'hidden'); else it.el.removeAttribute('visibility'); }
                  it.r = rr !== null ? rr : it.r0;
                } else if (it.kind === 'icon') {
                  it.el.setAttribute('transform', 'translate(' + sx + ',' + sy + ')');
                  it.r = it.r0;
                } else {
                  var ox = it.ox, oy = it.oy;
                  if (rr !== null) { var o = Math.min(rr, 200) * 0.7; ox += o; oy -= o; }
                  it.tx = sx + ox; it.ty = sy + oy;
                }
              });
              lines.forEach(function (ln) {
                var ax = hw + (ln.x1 - st.cx) * s, ay = hh - (ln.y1 - st.cy) * s, bx = hw + (ln.x2 - st.cx) * s, by = hh - (ln.y2 - st.cy) * s;
                // polyline (量子ジャンプの線) は真ん中にも点を置く (向きの三角を marker-mid で真ん中に出すため)
                if (ln.poly) ln.el.setAttribute('points', ax + ',' + ay + ' ' + (ax + bx) / 2 + ',' + (ay + by) / 2 + ' ' + bx + ',' + by);
                else { ln.el.setAttribute('x1', ax); ln.el.setAttribute('y1', ay); ln.el.setAttribute('x2', bx); ln.el.setAttribute('y2', by); }
              });
              declutter();
            }
            function boxHit(b, p) {
              return b.x < p.x + p.width + MARGIN && p.x < b.x + b.width + MARGIN && b.y < p.y + p.height + MARGIN && p.y < b.y + b.height + MARGIN;
            }
            // ラベルの箱 b が点 p と重なるか。円 (circle) は円の形で、印のアイコンは外径の正方形で見る。
            // 自分の点が円の中にあるとき (その天体の上の場所) は、その円とは重ならないものとする (どこに置いても重なるため)
            function pointHit(b, p, own) {
              if (p.kind === 'circle') {
                if (own && Math.pow(own.sx - p.sx, 2) + Math.pow(own.sy - p.sy, 2) < p.r * p.r) return false;
                var nx = Math.max(b.x, Math.min(p.sx, b.x + b.width)), ny = Math.max(b.y, Math.min(p.sy, b.y + b.height));
                return Math.pow(nx - p.sx, 2) + Math.pow(ny - p.sy, 2) < p.r * p.r;
              }
              return boxHit(b, { x: p.sx - p.r, y: p.sy - p.r, width: p.r * 2, height: p.r * 2 });
            }
            // ラベルを data-pri の順に置く。先に置いたラベルや、先に置いたラベルの点と重なるときは、右 (元の位置) → 左 → 上 → 下 の順に試し、
            // どこにも置けないときだけ隠す (点と <title> は残る)
            function declutter() {
              var placed = [], points = [];
              // 優先は書き換えで変わることがあるので、置くたびに読み直して並べ直す
              labels.forEach(function (lb) { lb.pri = num(lb.el, 'data-pri', 9); });
              labels.sort(function (a, b) { return a.pri - b.pri || b.sub - a.sub || a.no - b.no || a.i - b.i; });
              labels.forEach(function (lb) {
                var el = lb.el, it = lb.it, own = lb.pt;
                // ラベルの横の施設のアイコン (g.amenrow。マップタブだけ): ラベルと一緒に置き、一緒に隠す。重なりの判定はラベルとアイコンを合わせた箱で見る
                var am = el.parentNode ? el.parentNode.querySelector('g.amenrow') : null, aw = am ? num(am, 'data-aw', 0) : 0;
                if (am) am.setAttribute('visibility', 'hidden');
                // 使っていないラベル (data-off。現在位置の印の無いときのラベル) は隠し、重なりの判定に入れない
                if (el.getAttribute('data-off') === '1') { el.setAttribute('visibility', 'hidden'); return; }
                var pr = own ? own.r : 4, sx = own ? own.sx : it.sx, sy = own ? own.sy : it.sy;
                var tries = [[it.tx, it.ty, 'start'], [2 * sx - it.tx, it.ty, 'end'], [sx, sy - pr - 6, 'middle'], [sx, sy + pr + 22, 'middle']];
                el.removeAttribute('visibility');
                var ok = false;
                for (var t = 0; t < tries.length && !ok; t++) {
                  el.setAttribute('x', tries[t][0]); el.setAttribute('y', tries[t][1]); el.setAttribute('text-anchor', tries[t][2]);
                  var b = el.getBBox(), hit = false, ax = 0, ay = 0;
                  if (am) {
                    // アイコンはラベルの右 (ラベルが点の左 (end) のときはラベルの左)。高さはラベルの真ん中 (アイコンは 14px)
                    ax = tries[t][2] === 'end' ? b.x - 3 - aw : b.x + b.width + 3;
                    ay = b.y + b.height / 2 - 7;
                    var bx = Math.min(b.x, ax), by = Math.min(b.y, ay);
                    b = { x: bx, y: by, width: Math.max(b.x + b.width, ax + aw) - bx, height: Math.max(b.y + b.height, ay + 14) - by };
                  }
                  for (var k = 0; k < placed.length && !hit; k++) if (boxHit(b, placed[k])) hit = true;
                  for (var m = 0; m < points.length && !hit; m++) if (points[m] !== own && pointHit(b, points[m], own)) hit = true;
                  if (!hit) {
                    ok = true; placed.push(b);
                    if (am) { am.setAttribute('transform', 'translate(' + ax + ',' + ay + ')'); am.removeAttribute('visibility'); }
                  }
                }
                if (!ok) {
                  el.setAttribute('x', it.tx); el.setAttribute('y', it.ty); el.setAttribute('text-anchor', 'start');
                  el.setAttribute('visibility', 'hidden');
                }
                if (own) points.push(own);
              });
            }
            // 量子ジャンプの線 (両端 x1,y1 → x2,y2) のどちらかの端が今の表示の枠からはみ出していたら、線全体が枠の 8 割に収まるように中心と倍率を変える
            // (倍率は limits の範囲)。変えたら覚えた表示にも入れる (notify)。同じ線 (両端が同じ) については 1 回だけ (qtFitKey)
            var qtFitKey = null;
            function fitQt(x1, y1, x2, y2) {
              var key = Math.round(x1) + ',' + Math.round(y1) + ',' + Math.round(x2) + ',' + Math.round(y2);
              if (key === qtFitKey || !st.ready) return;
              qtFitKey = key;
              function inside(x, y) {
                var sx = st.W / 2 + (x - st.cx) * st.s, sy = st.H / 2 - (y - st.cy) * st.s;
                return sx >= 0 && sx <= st.W && sy >= 0 && sy <= st.H;
              }
              if (inside(x1, y1) && inside(x2, y2)) return;
              var dx = Math.abs(x2 - x1), dy = Math.abs(y2 - y1);
              var sx = dx > 0 ? st.W * 0.8 / dx : Infinity, sy = dy > 0 ? st.H * 0.8 / dy : Infinity;
              var ns = Math.min(sx, sy);
              st.cx = (x1 + x2) / 2; st.cy = (y1 + y2) / 2;
              if (isFinite(ns)) st.s = clamp(ns);
              render();
              notify();
            }
            var pendingFocus = null;
            function applyFocus(d) {
              st.cx = d.x; st.cy = d.y; st.s = st.max;
              render();
              notify();
            }
            function start() {
              if (st.ready || !measure()) return;
              st.ready = true;
              initial();
              render();
              if (pendingFocus) { var pf = pendingFocus; pendingFocus = null; applyFocus(pf); }
              // ページを作ったときに量子ジャンプの線が出ていれば、それにも同じ決まりを当てる
              var qg0 = svg.querySelector('g.qtgroup');
              if (qg0 && qg0.getAttribute('visibility') !== 'hidden')
                lines.forEach(function (ln) { if (ln.el.parentNode === qg0) fitQt(ln.x1, ln.y1, ln.x2, ln.y2); });
            }
            // 枠の大きさが変わったら (窓の大きさ・詳細の幅・見えていなかった枠が見えたとき) 置き直す
            function resized() {
              if (!st.ready) { start(); return; }
              if (!measure()) return;
              st.s = clamp(st.s);
              render();
            }
            // 拡大の上限でさらに拡大した: 操作の位置 (px, py) から ENTER_PX 以内の地図のある場所のうち、いちばん近いものをアプリへ知らせる (マップタブの図だけ)
            var lastEnter = 0;
            function enterNear(px, py) {
              if (saveKey === null || !(window.chrome && window.chrome.webview)) return;
              var now = Date.now();
              if (now - lastEnter < ENTER_GAP_MS) return;
              var best = null, bd = ENTER_PX;
              Array.prototype.forEach.call(svg.querySelectorAll('a[data-map]'), function (a) {
                var ic = a.querySelector('g.icon, circle[data-x]');
                if (!ic || !ic._zm) return;
                var d = Math.sqrt(Math.pow(ic._zm.sx - px, 2) + Math.pow(ic._zm.sy - py, 2));
                if (d <= bd) { bd = d; best = a; }
              });
              if (!best) return;
              lastEnter = now;
              window.chrome.webview.postMessage({ type: 'enterplace', rec: best.getAttribute('data-rec') });
            }
            function zoomAt(px, py, f) {
              if (!st.ready) return;
              var ns = clamp(st.s * f);
              if (ns === st.s) { if (f > 1 && st.s >= st.max) enterNear(px, py); return; }
              var wx = st.cx + (px - st.W / 2) / st.s, wy = st.cy - (py - st.H / 2) / st.s;
              st.s = ns;
              st.cx = wx - (px - st.W / 2) / ns; st.cy = wy + (py - st.H / 2) / ns;
              render();
              notify();
            }
            svg.addEventListener('wheel', function (e) {
              if (e.ctrlKey || !st.ready) return;
              e.preventDefault();
              var r = svg.getBoundingClientRect();
              var dy = e.deltaMode === 1 ? e.deltaY * 33 : e.deltaMode === 2 ? e.deltaY * st.H : e.deltaY;
              zoomAt(e.clientX - r.left, e.clientY - r.top, Math.pow(1.0015, -dy));
            }, { passive: false });
            var drag = null, moved = false;
            svg.addEventListener('pointerdown', function (e) {
              moved = false;
              if (e.button !== 0 || !st.ready) return;
              drag = { id: e.pointerId, x: e.clientX, y: e.clientY, cx: st.cx, cy: st.cy, on: false };
            });
            svg.addEventListener('pointermove', function (e) {
              if (!drag || e.pointerId !== drag.id) return;
              var dx = e.clientX - drag.x, dy = e.clientY - drag.y;
              if (!drag.on) {
                if (Math.abs(dx) + Math.abs(dy) < 4) return;
                drag.on = true; moved = true;
                try { svg.setPointerCapture(e.pointerId); } catch (err) { }
                svg.classList.add('dragging');
              }
              st.cx = drag.cx - dx / st.s; st.cy = drag.cy + dy / st.s;
              render();
            });
            function end(e) {
              if (!drag || e.pointerId !== drag.id) return;
              var was = drag.on;
              drag = null;
              svg.classList.remove('dragging');
              if (was) notify();
            }
            svg.addEventListener('pointerup', end);
            svg.addEventListener('pointercancel', end);
            // ドラッグの後のクリックは、リンクを開かせない
            svg.addEventListener('click', function (e) {
              if (moved) { e.preventDefault(); e.stopPropagation(); moved = false; }
            }, true);
            svg.addEventListener('dragstart', function (e) { e.preventDefault(); });
            if (ctl) ctl.addEventListener('click', function (e) {
              var b = e.target.closest ? e.target.closest('button') : null;
              if (!b || !st.ready) return;
              e.preventDefault();
              var act = b.getAttribute('data-act');
              if (act === 'fit') { fit(); render(); notify(); }
              else if (act === 'in') zoomAt(st.W / 2, st.H / 2, 1.5);
              else if (act === 'out') zoomAt(st.W / 2, st.H / 2, 1 / 1.5);
            });
            if (window.ResizeObserver) new ResizeObserver(resized).observe(svg);
            window.addEventListener('resize', resized);
            // マップタブの図 (data-save) だけ: アプリから書き換えのデータ ({ type: 'mapupdate', maps: [{ save, pts, here, mk }], near }) を受けて、
            // ページを作り直さずに書き換える (拡大率と表示の中心はそのまま。書き換えた後にラベルを置き直す)。名前は textContent で入れる
            function vis(el, on) { if (!el) return; if (on) el.removeAttribute('visibility'); else el.setAttribute('visibility', 'hidden'); }
            function setXY(el, x, y) {
              if (!el) return;
              el.setAttribute('data-x', x); el.setAttribute('data-y', y);
              if (el._zm) { el._zm.x = x; el._zm.y = y; }
            }
            function applyUpdate(m) {
              var groups = {};
              Array.prototype.forEach.call(svg.querySelectorAll('[data-rec]'), function (el) {
                var k = el.getAttribute('data-rec');
                (groups[k] = groups[k] || []).push(el);
              });
              // 各場所: 位置 (自転を入れたもの) とラベル
              (m.pts || []).forEach(function (pt) {
                (groups[pt.r] || []).forEach(function (el) {
                  if (el.tagName.toLowerCase() === 'a') {
                    Array.prototype.forEach.call(el.querySelectorAll('[data-x]'), function (t) { setXY(t, pt.x, pt.y); });
                    var tx = el.querySelector('text'), ti = el.querySelector('title');
                    if (tx) tx.textContent = pt.l;
                    if (ti) ti.textContent = pt.l;
                  } else setXY(el, pt.x, pt.y);
                });
              });
              // 中にいる場所: 赤 (here) と、ラベルの優先 0 (curtext)。ほかは元に戻す (data-opri)
              Object.keys(groups).forEach(function (k) {
                var on = k === m.here;
                groups[k].forEach(function (el) {
                  if (el.tagName.toLowerCase() !== 'a') { el.classList.toggle('here', on); return; }
                  var shape = el.querySelector('circle[data-x], g.icon');
                  if (shape) shape.classList.toggle('here', on);
                  var tx = el.querySelector('text[data-opri]');
                  if (tx) { tx.classList.toggle('curtext', on); tx.setAttribute('data-pri', on ? '0' : tx.getAttribute('data-opri')); }
                });
              });
              // 現在位置の印: 照準 (taken)・白抜きの丸 (est)・矢印 (moving) の出し入れ、位置、ラベル
              var mk = svg.querySelector('g.curmark');
              if (mk) {
                var k = m.mk ? m.mk.k : '';
                var cur = mk.querySelector('g.cur'), est = mk.querySelector('g.est'), line = mk.querySelector('line.qt'), lab = mk.querySelector('text.mk'), ti = mk.querySelector('title');
                vis(cur, k === 'taken'); vis(est, k === 'est'); vis(line, k === 'moving');
                mk.classList.toggle('taken', k === 'taken');   // 取った座標の印のときだけ押せる (gohere。推定の位置の印は押しても何も起きない)
                if (m.mk) {
                  setXY(cur, m.mk.x, m.mk.y); setXY(est, m.mk.x, m.mk.y); setXY(lab, m.mk.x, m.mk.y);
                  if (lab) { lab.textContent = m.mk.l; lab.removeAttribute('data-off'); }
                  if (ti) ti.textContent = m.mk.l;
                  lines.forEach(function (ln) {
                    if (ln.el !== line) return;
                    ln.x1 = m.mk.k === 'moving' ? m.mk.x1 : m.mk.x; ln.y1 = m.mk.k === 'moving' ? m.mk.y1 : m.mk.y; ln.x2 = m.mk.x; ln.y2 = m.mk.y;
                  });
                } else if (lab) lab.setAttribute('data-off', '1');
              }
              // 量子ジャンプの線 (qt: { x1, y1, x2, y2 } | null) と目的地を目立たせる (qtdest: record 名 | null)
              var qg = svg.querySelector('g.qtgroup');
              if (qg) {
                vis(qg, !!m.qt);
                if (m.qt) {
                  setXY(qg.querySelector('g.qtfrom'), m.qt.x1, m.qt.y1);
                  lines.forEach(function (ln) { if (ln.el.parentNode === qg) { ln.x1 = m.qt.x1; ln.y1 = m.qt.y1; ln.x2 = m.qt.x2; ln.y2 = m.qt.y2; } });
                }
              }
              // 線が出た・両端が変わったときは、はみ出していれば収まるように表示を変える (同じ線では 1 回だけ。下の render の前に中心と倍率を決める)
              if (m.qt) fitQt(m.qt.x1, m.qt.y1, m.qt.x2, m.qt.y2);
              Object.keys(groups).forEach(function (k) {
                groups[k].forEach(function (el) {
                  if (el.tagName.toLowerCase() !== 'a') return;
                  var shape = el.querySelector('circle[data-x], g.icon');
                  if (shape) shape.classList.toggle('qtdest', k === m.qtdest);
                });
              });
              // 線の先が契約の目標 (推定) のときの旗とラベル
              var gs = svg.querySelector('g.qtgoalslot');
              if (gs) {
                var gOn = !!(m.qt && m.qt.goal);
                var gIcon = gs.querySelector('g.qtgoal'), gText = gs.querySelector('text');
                vis(gIcon, gOn);
                if (gOn) { setXY(gIcon, m.qt.x2, m.qt.y2); setXY(gText, m.qt.x2, m.qt.y2); gText.removeAttribute('data-off'); }
                else if (gText) gText.setAttribute('data-off', '1');
              }
              // 最近の契約の目標 (objs: [{ x, y }]。入れ物の数まで)
              Array.prototype.forEach.call(svg.querySelectorAll('g.objslot'), function (slot, i) {
                var o = m.objs && i < m.objs.length ? m.objs[i] : null;
                var icon = slot.querySelector('g.obj'), tx = slot.querySelector('text');
                vis(icon, !!o);
                if (o) { setXY(icon, o.x, o.y); setXY(tx, o.x, o.y); tx.removeAttribute('data-off'); }
                else if (tx) tx.setAttribute('data-off', '1');
              });
              if (st.ready) render();
            }
            if (saveKey !== null && window.chrome && window.chrome.webview) {
              window.chrome.webview.addEventListener('message', function (e) {
                var d = e.data;
                // その位置 (ゲームの座標) を真ん中に、拡大の上限で出す (地図から星系図へ戻るとき・検索で選んだとき)。
                // 図が隠れていて大きさを測れていない (st.ready が false) ときは覚えておき、図が出たとき (start) に当てる
                if (d && d.type === 'mapfocus' && d.save === saveKey && isFinite(d.x) && isFinite(d.y)) {
                  if (!st.ready) { pendingFocus = d; return; }
                  applyFocus(d);
                  return;
                }
                if (!d || d.type !== 'mapupdate' || !d.maps) return;
                for (var i = 0; i < d.maps.length; i++) if (d.maps[i].save === saveKey) { applyUpdate(d.maps[i]); break; }
              });
            }
            start();
          }
          Array.prototype.forEach.call(document.querySelectorAll('svg.zmap'), setup);
          // 親子でまとめた表 (2026-11-05。［マップ］タブの【現在位置の近く】(MapPanel.NearInnerHtml)・場所の詳細の【距離】(GameDataQueryService.AppendDistances)):
          // 最初の欄が「▼ 」で始まる行を親、そのすぐ後に続く最初の欄が「　└」で始まる行をその子にし、子は初めは隠す。
          // 親の行 (リンクの外) を押すたびに開け閉めする (開いているときは「▲」。［詳細］の一覧の ▼ のまとめと同じ見た目と動き)。
          // 開いた親は最初の欄のリンク先 (無ければ文字) で覚え、【現在位置の近く】を書き換えた後も開いたままにする (ページを作り直したら閉じる)
          var foldOpen = {};
          function foldKey(tr) { var a = tr.cells[0].querySelector('a'); return a ? a.getAttribute('href') : tr.cells[0].textContent.slice(1); }
          function foldSet(tr, open) {
            var f = tr.cells[0].firstChild;
            if (f && f.nodeType === 3) f.data = (open ? '▲' : '▼') + f.data.slice(1);
            if (open) tr.classList.add('open'); else tr.classList.remove('open');
            tr._foldKids.forEach(function (k) { k.hidden = !open; });
          }
          // ▼ のまとめのある表 (table.foldtbl) の縞: 見えている行だけを上から数え、偶数番目の行に ev を付ける (色は CSS。ほかの表は今の nth-child のまま)
          function foldStripe(t) {
            var n = 0;
            Array.prototype.forEach.call(t.querySelectorAll('tbody > tr'), function (tr) {
              if (tr.hidden) { tr.classList.remove('ev'); return; }
              n++;
              if (n % 2 === 0) tr.classList.add('ev'); else tr.classList.remove('ev');
            });
          }
          function initFolds(root) {
            Array.prototype.forEach.call(root.querySelectorAll('table'), function (t) {
              var parent = null;
              Array.prototype.forEach.call(t.querySelectorAll('tbody > tr'), function (tr) {
                var c = tr.cells[0];
                if (!c) { parent = null; return; }
                if (parent && c.textContent.indexOf('　└') === 0) { tr.classList.add('fc'); parent._foldKids.push(tr); return; }
                parent = null;
                var f = c.firstChild;
                if (f && f.nodeType === 3 && /^[▼▲] /.test(f.data)) { parent = tr; tr._foldKids = []; }
              });
              var folds = false;
              Array.prototype.forEach.call(t.querySelectorAll('tbody > tr'), function (tr) {
                if (!tr._foldKids || tr._foldKids.length === 0) return;
                tr.classList.add('fp');
                folds = true;
                foldSet(tr, foldOpen[foldKey(tr)] === true);
              });
              if (folds) { t.classList.add('foldtbl'); foldStripe(t); }
            });
          }
          document.addEventListener('click', function (e) {
            var el = e.target;
            if (!el || !el.closest || el.closest('a')) return;
            var tr = el.closest('tr.fp');
            if (!tr || !tr._foldKids) return;
            var open = !tr.classList.contains('open');
            foldOpen[foldKey(tr)] = open;
            foldSet(tr, open);
            var t = tr.closest('table');
            if (t) foldStripe(t);
          });
          initFolds(document);
          // マップタブの【現在位置の近く】(aside.near) の書き換え: アプリが打ち消した HTML を入れる。無ければ隠す (詳細タブのページには aside.near が無い)
          if (window.chrome && window.chrome.webview) {
            window.chrome.webview.addEventListener('message', function (e) {
              var d = e.data;
              if (!d || d.type !== 'mapupdate') return;
              var a = document.querySelector('aside.near');
              if (!a) return;
              if (d.near) { a.innerHTML = d.near; a.hidden = false; initFolds(a); } else { a.innerHTML = ''; a.hidden = true; }
            });
          }
        })();
        """;
}
