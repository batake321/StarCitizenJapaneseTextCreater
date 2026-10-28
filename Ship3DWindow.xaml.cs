using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace StarCitizenJapaneseTextCreater;

// 船の 3D 表示の別ウィンドウ (モードレス)。
//   上の船の切り替え: ［詳細］タブの検索に準じる。名前の入力欄は 0.3 秒待ってから絞り込み (英語名の部分一致・大文字小文字無視)、
//     メーカー・種類・サイズの選択でも絞り込む。↓ で候補へ、Enter で最初の船に切り替える。候補を選ぶとその船に切り替える。
//     船の一覧は［詳細］の分類「船舶」と同じ (GetDetailShipList。英語名ごとに 1 つ)。種類は同じ船の種類の名前 (ShipKindGroups)、
//     メーカーは［詳細］の【基本】のメーカーと同じ名前 (GetShipManufacturerNames)
//   表示: キャッシュ (最新。Data.p4k が変わっていれば照合して同じだったもの) があればすぐ出す。無ければ自動で書き出さず［読み込み］を出し、
//     読み込みが終わったらこの窓に出す。出している間はその船の GLB を「表示中」にして、容量の上限・照合で消されないようにする
public partial class Ship3DWindow : Window
{
    private const string AnyValue = "(すべて)";

    // 候補の 1 行
    private sealed record Row(string Record, string Name, string Maker, string Kind, int Size, string Display);

    private List<Row> _rows = new();
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool _suppress;

    // 選んでいる船 (record・名前) と、出している船の短い名前 (出していなければ null)
    private string? _record;
    private string _name = "";
    private string? _shownShort;
    private Task? _webInit;
    private bool _webFailed;

    public Ship3DWindow(string? record, string name)
    {
        InitializeComponent();
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); RefreshList(); };
        var ex = Ship3DExports.Instance;
        ex.StateChanged += OnStateChanged;
        ex.Exported += OnExported;
        ex.PropertyChanged += OnExportsPropertyChanged;
        Closed += (_, _) =>
        {
            ex.StateChanged -= OnStateChanged;
            ex.Exported -= OnExported;
            ex.PropertyChanged -= OnExportsPropertyChanged;
            if (_shownShort != null) ex.ReleaseView(_shownShort);
            _shownShort = null;
            try { web3D.Dispose(); } catch { }
        };
        Loaded += async (_, _) =>
        {
            await LoadRowsAsync();
            if (record != null)
            {
                SelectInList(record);
                await SelectShipAsync(record, name);
            }
        };
    }

    // ===== 船の一覧と絞り込み =====

    private async Task LoadRowsAsync()
    {
        txtStatus.Text = "読み込み中...";
        try
        {
            var db = DetailPanel.DbPath();
            _rows = await Task.Run(() =>
            {
                if (!File.Exists(db)) return new List<Row>();
                using var q = GameDataQueryService.OpenReadOnly(db);
                var makers = q.GetShipManufacturerNames();
                return q.GetDetailShipList().Select(r =>
                {
                    var maker = makers.TryGetValue(r.Record, out var m) ? m : "";
                    var tags = string.Join(" / ", new[] { maker, r.Kind, r.Size > 0 ? $"S{r.Size}" : "" }.Where(t => t.Length > 0));
                    return new Row(r.Record, r.Name, maker, r.Kind, r.Size, tags.Length > 0 ? $"{r.Name}  [{tags}]" : r.Name);
                }).ToList();
            });
        }
        catch (Exception ex)
        {
            txtStatus.Text = $"船の一覧を読み込めませんでした: {ex.Message}";
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[3D モデル] 別ウィンドウの船の一覧を読み込めませんでした: {ex}");
            return;
        }
        if (_rows.Count == 0) { txtStatus.Text = "船の一覧がありません (設定タブの［インデックス構築］)"; return; }
        _suppress = true;
        try
        {
            // メーカーは名前の順、種類は一覧の並び (最初に出てきた順。［詳細］と同じ)、サイズは小さい順
            cmbMaker.ItemsSource = new[] { AnyValue }.Concat(_rows.Select(r => r.Maker).Where(m => m.Length > 0).Distinct(StringComparer.Ordinal)
                .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)).ToList();
            cmbKind.ItemsSource = new[] { AnyValue }.Concat(_rows.Select(r => r.Kind).Where(k => k.Length > 0).Distinct(StringComparer.Ordinal)).ToList();
            cmbSize.ItemsSource = new[] { AnyValue }.Concat(_rows.Where(r => r.Size > 0).Select(r => r.Size).Distinct().OrderBy(s => s).Select(s => $"S{s}")).ToList();
            cmbMaker.SelectedIndex = cmbKind.SelectedIndex = cmbSize.SelectedIndex = 0;
        }
        finally { _suppress = false; }
        RefreshList();
    }

    // 名前・メーカー・種類・サイズで候補を作り直す (選んでいる船が候補にあれば選んだままにする)
    private void RefreshList()
    {
        var query = txtQuery.Text.Trim();
        var maker = cmbMaker.SelectedItem as string;
        var kind = cmbKind.SelectedItem as string;
        var size = cmbSize.SelectedItem as string;
        var list = _rows.Where(r =>
                (maker == null || maker == AnyValue || r.Maker == maker)
                && (kind == null || kind == AnyValue || r.Kind == kind)
                && (size == null || size == AnyValue || $"S{r.Size}" == size)
                && (query.Length == 0 || r.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        _suppress = true;
        try
        {
            lstShips.ItemsSource = list;
            lstShips.SelectedItem = _record == null ? null : list.FirstOrDefault(r => r.Record.Equals(_record, StringComparison.OrdinalIgnoreCase));
        }
        finally { _suppress = false; }
        txtStatus.Text = $"{list.Count} 件";
    }

    private void SelectInList(string record)
    {
        if (lstShips.ItemsSource is not List<Row> list) return;
        _suppress = true;
        try { lstShips.SelectedItem = list.FirstOrDefault(r => r.Record.Equals(record, StringComparison.OrdinalIgnoreCase)); }
        finally { _suppress = false; }
    }

    private void TxtQuery_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void CmbFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        _searchTimer.Stop();
        RefreshList();
    }

    private void TxtQuery_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && lstShips.Items.Count > 0)
        {
            lstShips.SelectedIndex = 0;   // 選ぶとその船に切り替える
            (lstShips.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            _searchTimer.Stop();
            RefreshList();
            if (lstShips.Items.Count > 0) lstShips.SelectedIndex = 0;
            e.Handled = true;
        }
    }

    private async void LstShips_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || lstShips.SelectedItem is not Row row) return;
        await SelectShipAsync(row.Record, row.Name);
    }

    // ===== 船の切り替えと表示 =====

    // その船に切り替える (前の船の表示はやめる)。キャッシュがあれば出し、無ければ［読み込み］を出す
    private async Task SelectShipAsync(string record, string name)
    {
        if (string.Equals(record, _record, StringComparison.OrdinalIgnoreCase)) return;
        HideModel();
        _record = record;
        _name = string.IsNullOrWhiteSpace(name) ? Ship3DExports.ShortName(record) : name;
        Title = $"3D 表示 - {_name}";
        txtShip.Text = _name;
        UpdateBar();
        await ShowIfCachedAsync();
    }

    private bool _showing;

    // 選んでいる船のキャッシュがあれば出す (Data.p4k が変わっていれば照合してから)。無ければ［読み込み］を押すよう出す
    private async Task ShowIfCachedAsync()
    {
        var record = _record;
        if (record == null || _shownShort != null || _showing) return;
        var ex = Ship3DExports.Instance;
        var state = ex.StateOf(record);
        if (state == Ship3DCacheState.NoGame) { ShowOverlay("ゲームの Data.p4k が見つかりません。設定タブのゲームのフォルダとチャンネルを確かめてください。"); return; }
        if (state == Ship3DCacheState.None)
        {
            ShowOverlay(ex.IsExporting(record) ? "書き出し中です。終わったらここに出します。" : "この船の 3D モデルはまだ取り込んでいません。上の［読み込み］でゲームデータから取り込んでください。");
            return;
        }
        _showing = true;
        try
        {
            if (state != Ship3DCacheState.Fresh) ShowOverlay("照合中 (パッチで Data.p4k が変わったので、この船の元データを確かめています)...");
            var path = await ex.GetCachedPathAsync(record);
            if (!string.Equals(record, _record, StringComparison.OrdinalIgnoreCase) || _shownShort != null) return;
            if (path == null)
            {
                ShowOverlay("この船の元データがパッチで変わったので、キャッシュを消しました。上の［読み込み］で取り込み直してください。");
                return;
            }
            await ShowModelAsync(record);
        }
        finally
        {
            _showing = false;
            UpdateBar();
        }
    }

    private async Task ShowModelAsync(string record)
    {
        if (_webFailed) return;
        try { await (_webInit ??= Ship3DWeb.InitAsync(web3D)); }
        catch (Exception ex)
        {
            _webFailed = true;
            ShowOverlay($"3D 表示 (WebView2) を使えません: {ex.Message}");
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[3D モデル] 別ウィンドウの WebView2 の初期化に失敗しました: {ex.GetType().FullName} (HResult 0x{ex.HResult:X8}): {ex}");
            return;
        }
        if (!string.Equals(record, _record, StringComparison.OrdinalIgnoreCase) || _shownShort != null) return;
        var shortName = Ship3DExports.ShortName(record);
        Ship3DExports.Instance.AcquireView(shortName);
        _shownShort = shortName;
        try { Ship3DWeb.Show(web3D, shortName, _name); }
        catch (Exception ex)
        {
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[3D モデル] 別ウィンドウで表示できませんでした: {ex}");
            HideModel();
            ShowOverlay($"3D 表示できませんでした: {ex.Message}");
            return;
        }
        txtOverlay.Visibility = Visibility.Collapsed;
        web3D.Visibility = Visibility.Visible;
    }

    // 出している船の表示をやめる (読み込んだ GLB の分のメモリを放し、「表示中」を外す)
    private void HideModel()
    {
        if (_shownShort == null) return;
        var s = _shownShort;
        _shownShort = null;
        Ship3DWeb.Blank(web3D);
        web3D.Visibility = Visibility.Collapsed;
        Ship3DExports.Instance.ReleaseView(s);
    }

    private void ShowOverlay(string text)
    {
        txtOverlay.Text = text;
        txtOverlay.Visibility = Visibility.Visible;
        if (_shownShort == null) web3D.Visibility = Visibility.Collapsed;
    }

    // ［読み込み］: 予想を出して確かめてから裏で書き出す。終わったら (この窓がまだその船なら) ここに出す
    private async void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        var record = _record;
        if (record == null) return;
        btnImport.IsEnabled = false;
        var path = await Ship3DExports.Instance.ImportAsync(record, _name, this);
        UpdateBar();
        if (path != null && string.Equals(record, _record, StringComparison.OrdinalIgnoreCase)) await ShowIfCachedAsync();
    }

    // ［読み込み］の有効・無効と、その船の状態の文。
    // 使えるのは、キャッシュが無い (照合で消したものを含む) ときで、書き出し中でなく、CPU 使用率が 90% 未満のときだけ
    private void UpdateBar()
    {
        if (_record == null) { btnImport.IsEnabled = false; txtShipState.Text = ""; return; }
        var ex = Ship3DExports.Instance;
        var state = ex.StateOf(_record);
        var shortName = Ship3DExports.ShortName(_record);
        var job = ex.Jobs.FirstOrDefault(j => j.Short.Equals(shortName, StringComparison.OrdinalIgnoreCase));
        btnImport.IsEnabled = state == Ship3DCacheState.None && job == null && !ex.CpuHigh;
        txtShipState.Text = job != null ? $"書き出し中: {job.Text}"
            : state switch
            {
                Ship3DCacheState.Fresh => "キャッシュあり",
                Ship3DCacheState.NeedsVerify or Ship3DCacheState.Verifying => "照合中 (パッチで Data.p4k が変わったので元データを確かめています)",
                Ship3DCacheState.NoGame => "ゲームの Data.p4k が見つかりません",
                _ => ex.CpuHigh ? $"キャッシュなし。CPU 使用率 {ex.CpuPercent:F0}% のため取り込みを始められません (90% 未満になったら使えます)"
                                : "キャッシュなし (［読み込み］でゲームデータから取り込みます)",
            };
    }

    private void OnStateChanged()
    {
        UpdateBar();
        // 書き出しが終わった・照合で同じと分かったなど、出せるようになっていれば出す
        if (_record != null && _shownShort == null && Ship3DExports.Instance.StateOf(_record) == Ship3DCacheState.Fresh) _ = ShowIfCachedAsync();
    }

    private void OnExported(string shortName)
    {
        if (_record != null && _shownShort == null && Ship3DExports.ShortName(_record).Equals(shortName, StringComparison.OrdinalIgnoreCase)) _ = ShowIfCachedAsync();
    }

    // 書き出し中の進み具合の文 (1 秒ごとに全体の文が変わるときに出し直す)
    private void OnExportsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Ship3DExports.OverallText)) UpdateBar();
    }
}
