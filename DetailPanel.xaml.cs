using System.IO;
using System.Text;
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
    public static void Request(string? record, string name) => Requested?.Invoke(string.IsNullOrWhiteSpace(record) ? null : record, name ?? "");
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
    private sealed record ListItem(string Record, string Name, string Kind, int Size, string Display);

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

    private static string WorkDir()
    {
        var workDir = App.Config.WorkingDirectory;
        if (string.IsNullOrEmpty(workDir)) workDir = AppDomain.CurrentDomain.BaseDirectory;
        return workDir;
    }

    private static string DbPath() => Path.Combine(WorkDir(), "gamedata_cache.db");
    private static string TradeDbPath() => Path.Combine(WorkDir(), "trade_cache.db");

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
            webDetail.DefaultBackgroundColor = System.Drawing.Color.White;
            _webReady = true;
            if (_pendingHtml != null) { core.NavigateToString(_pendingHtml); _pendingHtml = null; }
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

    // 作業ディレクトリの app_log.txt に 1 行追記する (MainWindow.Log と同じファイル・同じ行の形。MainWindow.Log は private で呼べないため)。
    // 書き先は MainWindow.Log と同じ決め方 (App.Config.WorkingDirectory をそのまま使う。空なら今いるディレクトリからの相対パス)
    private static void AppendAppLog(string msg)
    {
        App.AppendStartupLog(App.Config.WorkingDirectory, msg);   // ほかの書き込みと重ならないよう App のロックを通す
    }

    // 詳細の文字 (GetItemDetailText の形) を出す。WebView2 では Markdown の表にして HTML で、使えなければ文字のまま
    private void ShowDetail(string text)
    {
        txtDetail.Text = DetailHtml.StripLinks(text);   // 文字の欄ではリンクは表示名だけ
        txtDetail.ScrollToHome();
        if (_webFailed) return;
        var html = DetailHtml.FromDetailText(text);
        if (_webReady) webDetail.CoreWebView2.NavigateToString(html);
        else _pendingHtml = html;
    }

    // ===== 開く (表のダブルクリック・船名) =====

    // 品目を出す。分類を (すべて) にして名前で検索したリストを出し、record の品目の詳細を出す。
    // record が無ければ名前で検索し、名前が同じ (大文字小文字無視) 候補、無ければ最初の候補を出す
    public async void ShowItem(string? record, string name)
    {
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
    // 資源は「名前 [資源]」、品目は item_index (無ければ items) の種類。同じ record は最初の 1 つだけ並べる。番号が範囲の外なら先頭を選ぶ
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
                    for (int i = 0; i < distinct.Count; i++)
                        types[i] = distinct[i].IsResource ? GameDataQueryService.ResourceDetailType : q?.ItemTypeOf(distinct[i].Record) ?? "";
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
    // 分類・リストはそのまま。リストにその record の行があれば選んだ状態にし、無ければ選択を外す
    private async void OpenLink(string record)
    {
        if (string.IsNullOrWhiteSpace(record)) return;
        if (!string.Equals(record, _detailRecord, StringComparison.OrdinalIgnoreCase)) PushHistory(CaptureState());
        SelectInList(record);
        try { await LoadDetailAsync(record); }
        catch (Exception ex) { _detailRecord = null; ShowDetail($"詳細を出せませんでした: {ex.Message}"); txtStatus.Text = ""; }
    }

    // 戻るの履歴の 1 件: 分類・種類 (選択肢・選んでいるもの・使えるか)・サイズ (同じ)・名前の欄・リストの中身・選んでいる行・表示している詳細の record
    private sealed record HistoryState(string Category, List<string> KindChoices, string? Kind, bool KindEnabled,
        List<string> SizeChoices, string? Size, bool SizeEnabled, string Query, List<ListItem> Items, ListItem? Selected, string? DetailRecord);

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
        (lstCandidates.ItemsSource as List<ListItem>)?.ToList() ?? new List<ListItem>(),
        useSelectedOverride ? selected : lstCandidates.SelectedItem as ListItem,
        _detailRecord);

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
        SetList(s.Items);
        _suppressSelection = true;
        try { lstCandidates.SelectedItem = s.Selected; }
        finally { _suppressSelection = false; }
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

    // 分類 (すべて) の検索: item_index のあいまい検索 (船・車両・武器・装備のすべて)。DB が無ければ null
    private static async Task<List<ListItem>?> SearchAllAsync(string query)
    {
        var db = DbPath();
        return await Task.Run(() =>
        {
            if (!File.Exists(db)) return null;
            using var q = GameDataQueryService.OpenReadOnly(db);
            return q.SearchDetailCandidates(query).Select(c => new ListItem(c.Record, c.Name, c.ItemType, 0, c.Display)).ToList();
        });
    }

    // リストを差し替える (選択を変えても詳細は開き直さない)。selectRecord があればその行を選んだ状態にする
    private void SetList(List<ListItem> items, string? selectRecord = null)
    {
        _suppressSelection = true;
        try
        {
            lstCandidates.ItemsSource = items;
            lstCandidates.SelectedItem = selectRecord == null ? null
                : items.FirstOrDefault(i => i.Record.Equals(selectRecord, StringComparison.OrdinalIgnoreCase));
        }
        finally { _suppressSelection = false; }
        ShowListArea(items.Count > 0);
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
        if (lstCandidates.ItemsSource is not List<ListItem> items) return;
        _suppressSelection = true;
        try { lstCandidates.SelectedItem = items.FirstOrDefault(i => i.Record.Equals(record, StringComparison.OrdinalIgnoreCase)); }
        finally { _suppressSelection = false; }
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
        _loadingCount++;
        try
        {
            // お店・場所・星系 (その詳細と、販売場所のリンクに使う)。品目の詳細では、木を作れなくても詳細はリンク無しで出す
            try { tree = await GetShopTreeAsync(); }
            catch when (!isTreeRecord) { tree = null; }
            (text, itemType) = await Task.Run(() =>
            {
                if (isTreeRecord) return (tree != null ? GameDataQueryService.ShopTreeDetailText(tree, record) : null, "");
                if (!File.Exists(db)) return ((string?)null, "");
                using var q = GameDataQueryService.OpenReadOnly(db);
                return (q.GetItemDetailText(record, shopTree: tree), q.ItemTypeOf(record));
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
                return q.GetItemDetailText(record, uex, tree);
            });
            if (version != _detailVersion || again == null) return;
            ShowDetail(again);
        }
        catch { }   // UEX が取れなくても保存データの詳細のまま
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

    // 「|」で始まる行 (元から Markdown の表) の中のリンクを <a> にする (リンクの外はそのまま)
    private static string TableLineLinks(string line) =>
        LinkRegex.Replace(line, m => LinkHtml(m.Groups[1].Value, Escape(m.Groups[2].Value)));

    public static string FromDetailText(string text) => Wrap(Markdig.Markdown.ToHtml(ToMarkdown(text ?? ""), Pipeline));

    public static string ToMarkdown(string text)
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
    private static string Wrap(string body) => $$"""
        <!DOCTYPE html><html><head><meta charset="utf-8">
        <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'">
        <style>
        body { font-family: 'Yu Gothic UI','Meiryo','Segoe UI',sans-serif; font-size: 13px; color: #212121; background: #FFFFFF;
               margin: 8px 10px; line-height: 1.5; word-wrap: break-word; }
        h3 { font-size: 1.05em; margin: 1em 0 0.4em; padding-bottom: 2px; border-bottom: 1px solid #DDD; }
        h3:first-child { margin-top: 0.2em; }
        p { margin: 0.3em 0; }
        table { border-collapse: collapse; margin: 0.3em 0 0.6em; }
        th, td { border: 1px solid #BBB; padding: 3px 8px; vertical-align: top; text-align: left; }
        th { background: #E8E8E8; font-weight: 600; }
        tr:nth-child(even) td { background: #F8F8F8; }
        .em { color: #1565C0; }
        </style></head><body>{{body}}</body></html>
        """;
}
