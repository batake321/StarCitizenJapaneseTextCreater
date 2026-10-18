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
//   リストで選ぶと詳細を出す。名前の欄で ↓ を押すとリストへ、Enter でリストの最初の品目を開く
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

    // 分類 (表と同じ分け方)。(すべて) は名前で全部から探す
    private const string CategoryAll = "(すべて)";
    private const string CategoryShip = "船舶";
    private const string CategoryShipWeapon = "船舶武器", CategoryShipGear = "船舶装備", CategoryPersonalWeapon = "個人武器", CategoryPersonalGear = "個人装備";
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
        cmbCategory.ItemsSource = new[] { CategoryAll, CategoryShip, CategoryShipWeapon, CategoryShipGear, CategoryPersonalWeapon, CategoryPersonalGear };
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
            // 中身は自分で作った HTML だけ。利用者の操作でほかへ移る・新しい窓を開くことはさせない
            core.NavigationStarting += (_, e) => { if (e.IsUserInitiated) e.Cancel = true; };
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
        try { File.AppendAllText(Path.Combine(App.Config.WorkingDirectory, "app_log.txt"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n", Encoding.UTF8); } catch { }
    }

    // 詳細の文字 (GetItemDetailText の形) を出す。WebView2 では Markdown の表にして HTML で、使えなければ文字のまま
    private void ShowDetail(string text)
    {
        txtDetail.Text = text;
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
        _searchTimer.Stop();
        SetQueryAndCategoryAll(name);
        try
        {
            var items = await SearchAllAsync(name);
            if (items != null) SetList(items, selectRecord: record);
            if (record == null)
            {
                var pick = items?.FirstOrDefault(c => c.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) ?? items?.FirstOrDefault();
                if (pick == null) { _detailVersion++; ShowDetail($"「{name}」が見つかりませんでした。"); return; }
                record = pick.Record;
                SelectInList(record);
            }
            await LoadDetailAsync(record);
        }
        catch (Exception ex) { ShowDetail($"詳細を出せませんでした: {ex.Message}"); txtStatus.Text = ""; }
    }

    // 名前欄に name を入れて検索し、リストを出す (品目は開かない)。下の詳細欄の前の品目は消す
    public async void ShowSearch(string name)
    {
        _detailVersion++;
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
        var db = DbPath();
        var rows = await Task.Run(() =>
        {
            if (!File.Exists(db)) return null;
            using var q = GameDataQueryService.OpenReadOnly(db);
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
        if (lstCandidates.SelectedItem is ListItem item) Open(item);
    }

    private void LstCandidates_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && lstCandidates.SelectedItem is ListItem item) { Open(item); e.Handled = true; }
    }

    // リストの品目の詳細を出す (名前の欄は変えない。変えるとリストの絞り込みが変わるため)。record が無ければ名前で引き直す
    private async void Open(ListItem item)
    {
        try
        {
            var record = item.Record;
            if (record.Length == 0)
            {
                var found = await SearchAllAsync(item.Name);
                record = found?.FirstOrDefault(c => c.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase))?.Record ?? found?.FirstOrDefault()?.Record ?? "";
                if (record.Length == 0) { _detailVersion++; ShowDetail($"「{item.Name}」が見つかりませんでした。"); return; }
            }
            await LoadDetailAsync(record);
        }
        catch (Exception ex) { ShowDetail($"詳細を出せませんでした: {ex.Message}"); txtStatus.Text = ""; }
    }

    // ===== 詳細 =====

    private async Task LoadDetailAsync(string record)
    {
        var version = ++_detailVersion;
        var status = txtStatus.Text;
        if (status == LoadingText) status = "";   // 前の読み込みの「読み込み中...」は戻さない
        // 本来の表示は、ほかに進行中の読み込みが無ければ今の表示。進行中の読み込みがあれば、その読み込みの結果 (覚えてあるもの) のまま
        if (_loadingCount == 0) _statusAfterLoading = status;
        txtStatus.Text = LoadingText;
        var db = DbPath();
        string? text;
        string itemType = "";
        string? error = null;
        _loadingCount++;
        try
        {
            (text, itemType) = await Task.Run(() =>
            {
                if (!File.Exists(db)) return ((string?)null, "");
                using var q = GameDataQueryService.OpenReadOnly(db);
                return (q.GetItemDetailText(record), q.ItemTypeOf(record));
            });
        }
        catch (Exception ex) { text = null; error = $"読み込めませんでした: {ex.Message}"; }
        finally { _loadingCount--; }
        if (version == _detailVersion && error != null) ShowStatus(error);
        // 最後の読み込みなら「読み込み中...」を本来の表示に戻す。ほかの読み込み (分類・詳細) がまだ進行中なら、その「読み込み中...」を残す
        RestoreStatusIfIdle();
        if (version != _detailVersion) return;
        if (text == null) { ShowDetail(File.Exists(db) ? "品目が見つかりませんでした。" : "gamedata_cache.db がありません (設定タブの［インデックス構築］)"); return; }
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
                return q.GetItemDetailText(record, uex);
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
                md.AppendLine(t.Replace("\u3000", "&#12288;"));
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

    // 文字を Markdown に入れる: ミッションタブと同じ変換をし、強調は色の付いた span にする。記号は \ で打ち消す (Markdown の書式にしない)
    private static string Inline(string s)
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
