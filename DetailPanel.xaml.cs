using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace StarCitizenJapaneseTextCreater;

// ［詳細］タブ。船・車両・武器・装備の詳細を出す (GameDataQueryService.GetItemDetailText)。
//   名前の欄: 入力すると 0.3 秒待ってから候補を出す (GameDataQueryService.SearchDetailCandidates)。候補をダブルクリック / Enter で開く
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

    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool _suppressSearch;
    private int _searchVersion, _detailVersion;

    public DetailPanel()
    {
        InitializeComponent();
        _searchTimer.Tick += async (_, _) =>
        {
            _searchTimer.Stop();
            try { await SearchAsync(); }
            catch (Exception ex) { txtStatus.Text = $"検索できませんでした: {ex.Message}"; }
        };
    }

    private static string DbPath()
    {
        var workDir = App.Config.WorkingDirectory;
        if (string.IsNullOrEmpty(workDir)) workDir = AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(workDir, "gamedata_cache.db");
    }

    // 品目を出す。record が無ければ名前で検索し、名前が同じ (大文字小文字無視) 候補、無ければ最初の候補を出す
    public async void ShowItem(string? record, string name)
    {
        _suppressSearch = true;
        txtQuery.Text = name;
        _suppressSearch = false;
        lstCandidates.Visibility = Visibility.Collapsed;
        try
        {
            if (record == null)
            {
                var cands = await Task.Run(() => Search(name));
                var pick = cands.FirstOrDefault(c => c.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) ?? cands.FirstOrDefault();
                if (pick == null) { txtDetail.Text = $"「{name}」が見つかりませんでした。"; return; }
                record = pick.Record;
            }
            await LoadDetailAsync(record);
        }
        catch (Exception ex) { txtDetail.Text = $"詳細を出せませんでした: {ex.Message}"; txtStatus.Text = ""; }
    }

    // 名前欄に name を入れて検索し、候補の一覧を出す (品目は開かない。入力したときと同じ検索をすぐ行う)。
    // 下の詳細欄の前の品目は消す (読み込み中・値段の取り直し中の前の品目も、版を進めて出さない)
    public async void ShowSearch(string name)
    {
        _detailVersion++;
        txtDetail.Text = "";
        _searchTimer.Stop();
        _suppressSearch = true;
        txtQuery.Text = name;
        _suppressSearch = false;
        try { await SearchAsync(); }
        catch (Exception ex) { txtStatus.Text = $"検索できませんでした: {ex.Message}"; }
    }

    private static List<DetailCandidate> Search(string query)
    {
        var db = DbPath();
        if (!File.Exists(db)) return new List<DetailCandidate>();
        using var q = GameDataQueryService.OpenReadOnly(db);
        return q.SearchDetailCandidates(query);
    }

    private void TxtQuery_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSearch) return;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private async Task SearchAsync()
    {
        var version = ++_searchVersion;
        var query = txtQuery.Text.Trim();
        if (query.Length == 0) { lstCandidates.Visibility = Visibility.Collapsed; txtStatus.Text = ""; return; }
        List<DetailCandidate> cands;
        try { cands = await Task.Run(() => Search(query)); }
        catch (Exception ex) { txtStatus.Text = $"検索できませんでした: {ex.Message}"; return; }
        if (version != _searchVersion) return;
        lstCandidates.ItemsSource = cands;
        lstCandidates.Visibility = cands.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        txtStatus.Text = cands.Count > 0 ? $"候補 {cands.Count} 件" : "候補なし";
    }

    private void TxtQuery_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && lstCandidates.Items.Count > 0)
        {
            lstCandidates.Visibility = Visibility.Visible;
            lstCandidates.SelectedIndex = 0;
            (lstCandidates.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (lstCandidates.Items.Count > 0 && lstCandidates.Items[0] is DetailCandidate c) Open(c);
            e.Handled = true;
        }
    }

    private void LstCandidates_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (lstCandidates.SelectedItem is DetailCandidate c) Open(c);
    }

    private void LstCandidates_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && lstCandidates.SelectedItem is DetailCandidate c) { Open(c); e.Handled = true; }
    }

    private async void Open(DetailCandidate c)
    {
        _suppressSearch = true;
        txtQuery.Text = c.Name;
        _suppressSearch = false;
        lstCandidates.Visibility = Visibility.Collapsed;
        try { await LoadDetailAsync(c.Record); }
        catch (Exception ex) { txtDetail.Text = $"詳細を出せませんでした: {ex.Message}"; txtStatus.Text = ""; }
    }

    private async Task LoadDetailAsync(string record)
    {
        var version = ++_detailVersion;
        txtStatus.Text = "読み込み中...";
        var db = DbPath();
        string? text;
        string itemType = "";
        try
        {
            (text, itemType) = await Task.Run(() =>
            {
                if (!File.Exists(db)) return ((string?)null, "");
                using var q = GameDataQueryService.OpenReadOnly(db);
                return (q.GetItemDetailText(record), q.ItemTypeOf(record));
            });
        }
        catch (Exception ex) { text = null; txtStatus.Text = $"読み込めませんでした: {ex.Message}"; }
        if (version != _detailVersion) return;
        if (text == null) { txtDetail.Text = File.Exists(db) ? "品目が見つかりませんでした。" : "gamedata_cache.db がありません (設定タブの［インデックス構築］)"; txtStatus.Text = ""; return; }
        txtDetail.Text = text;
        txtDetail.ScrollToHome();
        txtStatus.Text = "";

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
            txtDetail.Text = again;
        }
        catch { }   // UEX が取れなくても保存データの詳細のまま
    }
}
