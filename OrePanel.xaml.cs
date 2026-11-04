using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace StarCitizenJapaneseTextCreater;

// ログの解析 (後半で接続) が［鉱石］へ渡す商品売買 1 件。GameLogParser の型には依存しない。
//   LogTime: 行頭の時刻の文字 (UTC。"2026-10-15T13:41:22.633")、Kind: "buy" | "sell"、QuantityScu: SCU (読めなければ null)、
//   QuantityRaw: ログの quantity の文字そのまま (主キーの一部)、TotalAuec: 合計 (buy の price / sell の amount)、Result: '' が成功 (失敗は結果の文字)
public sealed record OreTradeRecord(string LogTime, string PlayerId, string ShopId, string KioskId, string ShopName, string Kind,
    string ResourceUuid, string ResourceRecord, double? QuantityScu, string QuantityRaw, double? TotalAuec, string TransactionMode, string Result);

// 同じく採掘 1 件。MineralClass は手掘りの鉱石のクラス名 (Harvestable_Mineral_1H_<X>)、EntityId は個体の ID、Method: "hand" | "ship" | "geo" (ATLS GEO の入れ物から回収) | "manual"。
// ResourceUuid が空なら、MineralClass から resource_types の ResourceType.<X> を引いて入れる (OreService.ResolveMineralResource)
public sealed record OreMiningRecord(string LogTime, string PlayerName, string MineralClass, string EntityId, double Quantity, string Unit,
    int? Quality, string Method, string ResourceUuid = "", string ResourceRecord = "");

// ［鉱石］タブ。在庫 (ore_stock) の表と追加・編集・削除、売買の記録 (commodity_trades)・採掘の記録 (mining_log) の表、利益の再計算。
// DB は作業ディレクトリ (WorkDirProvider。MainWindow が渡す) の trade_cache.db (OreService)。資源の名前・グループは同じディレクトリの
// gamedata_cache.db (resource_types) と translations.db から引く。初めて表示したときに読み、MainWindow が Reload で読み直す
public partial class OrePanel : UserControl
{
    private const string AllGroups = "(すべて)";

    public Func<string>? WorkDirProvider { get; set; }

    private readonly OreService _service = new();
    private readonly object _importLock = new();   // ImportLogRecords (裏のスレッドから来ることがある) を 1 つずつ行う
    private bool _loadedOnce;
    private List<OreResource> _resources = new();                                           // resource_types の全件 (名前の表示用)
    private Dictionary<string, OreResource> _byUuid = new(StringComparer.OrdinalIgnoreCase);
    private List<OreResource> _oreList = new();                                             // 追加パネルに出す資源 (OreService.GetOreResourceList)
    private List<OreStockRow> _stockRows = new();
    private readonly Dictionary<string, (string Uuid, string Record)?> _mineralCache = new(StringComparer.OrdinalIgnoreCase);

    public OrePanel()
    {
        InitializeComponent();
        DataGridCellCopy.Attach(dgStock);
        cmbUnit.ItemsSource = new[] { OreService.UnitScu, OreService.UnitPiece };
        cmbUnit.SelectedIndex = 0;
        tblTrades.SetColumns(TradeColumns());
        tblMining.SetColumns(MiningColumns());
        // 資源の列のダブルクリック → ［詳細］タブにその資源 (採掘できる場所) を出す。在庫の表は資源の列 (読み取り専用) だけ
        dgStock.MouseDoubleClick += DgStock_MouseDoubleClick;
        tblTrades.CellDoubleClicked += (column, source, _) => { if (column == TradeResourceColumn && source is CommodityTrade t) OpenResourceDetail(t.ResourceUuid, t.ResourceRecord); };
        tblMining.CellDoubleClicked += (column, source, _) => { if (column == MiningResourceColumn && source is MiningLog m) OpenResourceDetail(m.ResourceUuid, m.ResourceRecord); };
        Loaded += (_, _) =>
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            ReloadAll();
        };
        // シグネチャーの場所: 画面に出たとき、前に選んでから現在位置 (/showlocation) が変わっていれば選び直す
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true && _loadedOnce && App.Config.MapCurrent.Utc != _sigPresetUtc) _ = PreselectSigPlaceAsync();
        };
    }

    private string WorkDir()
    {
        var d = WorkDirProvider?.Invoke();
        if (string.IsNullOrEmpty(d)) d = App.Config.WorkingDirectory;
        if (string.IsNullOrEmpty(d)) d = AppDomain.CurrentDomain.BaseDirectory;
        return d;
    }

    private string GamedataDbPath => Path.Combine(WorkDir(), "gamedata_cache.db");
    private string TranslationsDbPath => Path.Combine(WorkDir(), "translations.db");

    // 資源の一覧・在庫・記録をすべて読み直す。MainWindow から、バックアップの取り込み・インデックス構築・訳の変更・作業ディレクトリの変更の後に呼ぶ。
    // 一度も表示していなければ何もしない (表示したときに読む)。別のスレッドからは UI のスレッドへ回す
    public void Reload()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Reload);
            return;
        }
        if (!_loadedOnce) return;
        ReloadAll();
    }

    private void ReloadAll()
    {
        try
        {
            LoadResources();
            RefreshGroupCombo();
            RefreshResourceCandidates();
            RefreshStock();
            RefreshTables();
        }
        catch (Exception ex)
        {
            txtOreStatus.Text = $"読み込みエラー: {ex.Message}";
        }
        LoadSignatureData();
    }

    // === シグネチャー (スキャナーの数字 → 鉱石の種類・岩の数・格。OreSignatureCalc) ===

    private OreSignatureCalc.Data _sigData = OreSignatureCalc.Data.Empty;
    private Dictionary<string, string> _gradeLabels = new(StringComparer.OrdinalIgnoreCase);
    private double _sigPresetUtc = double.NaN;   // 場所を現在位置で選んだときの App.Config.MapCurrent.Utc (まだなら NaN)
    private int _sigPresetVersion;

    // ore_signatures・ore_locations と格の訳を読み直し、場所の一覧を作り直して、現在位置で場所を選び直す
    private void LoadSignatureData()
    {
        try
        {
            _sigData = OreSignatureCalc.Load(GamedataDbPath);
            _gradeLabels = OreSignatureCalc.LoadGradeLabels(TranslationsDbPath);
        }
        catch (Exception ex)
        {
            _sigData = OreSignatureCalc.Data.Empty;
            txtSigLocation.Text = $"シグネチャーのデータの読み込みエラー: {ex.Message}";
        }
        var prev = cmbSigPlace.SelectedItem as OreSignatureCalc.Place;
        var items = new List<OreSignatureCalc.Place> { OreSignatureCalc.Place.None };
        items.AddRange(_sigData.Places);
        cmbSigPlace.ItemsSource = items;
        cmbSigPlace.SelectedItem = prev != null ? items.FirstOrDefault(p => p.Equals(prev)) ?? OreSignatureCalc.Place.None : OreSignatureCalc.Place.None;
        RefreshSignature();
        // 現在位置で選ぶのは初めの 1 回と、現在位置が変わったとき・表が入ったときだけ (読み直しのたびに手で選んだ場所を上書きしない)
        if (_sigPresetVersion == 0 || App.Config.MapCurrent.Utc != _sigPresetUtc || txtSigLocation.Text == SigMissingText) _ = PreselectSigPlaceAsync();
    }

    // 現在位置 (App.Config.MapCurrent。［マップ］の［現在位置］で /showlocation から取ったもの) の近くの天体 (CurrentLocation.NearBody) の名前が
    // 場所の一覧の名前と同じなら、それを選ぶ。同じ名前が無ければ選ばない (［場所を選ばない］にする)。現在位置が無ければ何もしない
    private async Task PreselectSigPlaceAsync()
    {
        var version = ++_sigPresetVersion;
        var cur = App.Config.MapCurrent;
        var utc = cur.Utc;
        if (!(utc > 0))
        {
            _sigPresetUtc = utc;
            txtSigLocation.Text = _sigData.HasSignatures ? "現在位置がまだありません (［マップ］の［現在位置］で取ると、近くの天体を場所に選びます)" : SigMissingText;
            return;
        }
        var db = GamedataDbPath;
        var tradeDb = Path.Combine(WorkDir(), "trade_cache.db");
        string? body = null, system = null;
        try
        {
            (body, system) = await Task.Run<(string?, string?)>(() =>
            {
                if (!File.Exists(db)) return (null, null);
                using var q = GameDataQueryService.OpenReadOnly(db);
                var tree = q.BuildShopTree(tradeDb);
                if (tree.IsEmpty) return (null, null);
                var pos = (cur.X, cur.Y, cur.Z);
                var effSystem = cur.System is { Length: > 0 } es ? tree.Systems.FirstOrDefault(s => s.System.Equals(es, StringComparison.OrdinalIgnoreCase)) : null;
                var loc = CurrentLocation.Locate(tree, pos, utc, MapPanel.SystemNames, null, fresh: false, effSystem);
                if (loc == null) return (null, null);
                var nb = CurrentLocation.NearBody(tree, loc.System, pos);
                return (nb?.Body.Name, loc.System.System);
            });
        }
        catch (Exception ex)
        {
            if (version != _sigPresetVersion) return;
            _sigPresetUtc = utc;
            txtSigLocation.Text = $"現在位置から場所を選べませんでした: {ex.Message}";
            return;
        }
        if (version != _sigPresetVersion) return;
        _sigPresetUtc = utc;
        var match = OreSignatureCalc.MatchPlace(_sigData.Places, body, system);
        cmbSigPlace.SelectedItem = match ?? OreSignatureCalc.Place.None;
        txtSigLocation.Text = !_sigData.HasSignatures ? SigMissingText
            : body == null ? "現在位置の近くに天体がありません (場所は選んでいません)"
            : match != null ? $"現在位置の近くの天体: {body} (場所に選びました)"
            : $"現在位置の近くの天体: {body} (場所の一覧に無いので選んでいません)";
    }

    private const string SigMissingText = "シグネチャーの表 (ore_signatures) がまだありません。起動後の裏の作り直しかインデックス構築の後に入ります";

    private void Signature_TextChanged(object sender, TextChangedEventArgs e) => RefreshSignature();
    private void SigPlace_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshSignature();

    // 入れた数字と選んだ場所で結果を出し直す
    private void RefreshSignature()
    {
        if (dgSignature == null || txtSignature == null) return;
        var place = cmbSigPlace.SelectedItem as OreSignatureCalc.Place;
        if (place != null && place.IsNone) place = null;
        if (!_sigData.HasSignatures)
        {
            txtSigSummary.Text = SigMissingText;
            dgSignature.ItemsSource = null;
            return;
        }
        var text = txtSignature.Text.Normalize(System.Text.NormalizationForm.FormKC).Trim();   // 全角の数字も受け付ける
        if (text.Length == 0)
        {
            txtSigSummary.Text = "";
            dgSignature.ItemsSource = null;
            return;
        }
        if (!double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n) || n <= 0)
        {
            txtSigSummary.Text = "シグネチャーは 0 より大きい数で入れてください。";
            dgSignature.ItemsSource = null;
            return;
        }
        var result = OreSignatureCalc.Compute(_sigData, n, place, g => _gradeLabels.TryGetValue(g, out var l) ? l : g);
        txtSigSummary.Text = string.Join(Environment.NewLine, result.Messages);
        dgSignature.ItemsSource = result.Rows;
    }

    // resource_types の全件 (名前の表示用) と、追加パネルに出す資源の一覧
    private void LoadResources()
    {
        _resources = OreService.LoadResources(GamedataDbPath, TranslationsDbPath);
        _byUuid = new Dictionary<string, OreResource>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _resources) _byUuid.TryAdd(r.Uuid, r);
        _oreList = _resources.Where(x => x.InOreList)
            .OrderBy(x => x.GroupName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Display, StringComparer.OrdinalIgnoreCase).ToList();
        _mineralCache.Clear();
    }

    // 資源の uuid → 「日本語 (英語)」。resource_types に無ければ record_name の "ResourceType." を除いた名前、それも無ければ uuid の先頭 8 文字
    private string ResourceDisplay(string uuid, string record)
    {
        if (uuid.Length > 0 && _byUuid.TryGetValue(uuid, out var r)) return r.Display;
        if (record.Length > 0) return OreService.ShortRecord(record);
        return uuid.Length > 8 ? uuid[..8] : uuid;
    }

    // 資源を［詳細］タブに出す (DetailPanel.Request)。record は resource_types の record 名 (無ければ行の record 名)、名前は英語名
    // (無ければ record 名の "ResourceType." を除いたもの)。資源の分からない行 (資源を引けていない採掘の行など) は何もしない
    private void OpenResourceDetail(string uuid, string record)
    {
        if (uuid.Length > 0 && _byUuid.TryGetValue(uuid, out var r))
        {
            DetailPanel.Request(r.RecordName, r.NameEn ?? OreService.ShortRecord(r.RecordName));
            return;
        }
        if (record.StartsWith("ResourceType.", StringComparison.OrdinalIgnoreCase)) DetailPanel.Request(record, OreService.ShortRecord(record));
    }

    // 在庫の表の資源の列 (1 列目。読み取り専用) のダブルクリック。ほかの列 (品質・量・メモの書き換え) の動きは変えない
    private void DgStock_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        DependencyObject? d = e.OriginalSource as DependencyObject;
        while (d != null && d is not DataGridCell) d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        if (d is not DataGridCell cell || dgStock.Columns.Count == 0 || cell.Column != dgStock.Columns[0] || cell.DataContext is not OreStockRow row) return;
        OpenResourceDetail(row.Model.ResourceUuid, row.Model.ResourceRecord);
    }

    private string GroupDisplay(string uuid) => uuid.Length > 0 && _byUuid.TryGetValue(uuid, out var r) ? r.GroupName : "";

    // === 在庫 ===

    private void RefreshStock()
    {
        if (dgStock == null) return;
        try
        {
            // 欄を編集したまま移った等で行の編集が開いていると ItemsSource の入れ直しが失敗しうるので先に確定する
            dgStock.CommitEdit(DataGridEditingUnit.Row, true);
            _service.SetCacheDir(WorkDir());
            var stock = _service.LoadStock();
            _stockRows = stock.Select(s => new OreStockRow(s)
            {
                ResourceDisplay = ResourceDisplay(s.ResourceUuid, s.ResourceRecord),
                GroupName = GroupDisplay(s.ResourceUuid),
                OnQualityChanged = StockQuality_Changed,
                OnQuantityChanged = StockQuantity_Changed,
                OnNotesChanged = StockNotes_Changed,
            }).ToList();
            var selectedId = (dgStock.SelectedItem as OreStockRow)?.Id;
            dgStock.ItemsSource = null;
            dgStock.ItemsSource = _stockRows;
            if (selectedId != null) dgStock.SelectedItem = _stockRows.FirstOrDefault(r => r.Id == selectedId.Value);
            txtOreStatus.Text = _resources.Count == 0
                ? $"在庫: {stock.Count} 件 (gamedata_cache.db の資源の種類 (resource_types) がありません。資源の名前・一覧は出ません)"
                : _oreList.Count == 0
                    ? $"在庫: {stock.Count} 件 (資源グループがまだ無いので追加の一覧は空です。起動後の裏の取り出しかインデックス構築の後に入ります)"
                    : $"在庫: {stock.Count} 件";
        }
        catch (Exception ex)
        {
            txtOreStatus.Text = $"読み込みエラー: {ex.Message}";
        }
    }

    private void RefreshGroupCombo()
    {
        var prev = cmbGroup.SelectedItem as string;
        var groups = new List<string> { AllGroups };
        foreach (var g in _oreList.Select(x => x.GroupName).Distinct(StringComparer.OrdinalIgnoreCase))
            if (g.Length > 0) groups.Add(g);
        cmbGroup.ItemsSource = groups;
        cmbGroup.SelectedItem = prev != null && groups.Contains(prev) ? prev : AllGroups;
    }

    private void Group_Changed(object sender, SelectionChangedEventArgs e) => RefreshResourceCandidates();
    private void ResourceSearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshResourceCandidates();

    // グループと検索語 (日本語名・英語名・レコード名) で資源の一覧を絞って候補コンボに出す
    private void RefreshResourceCandidates()
    {
        if (cmbResource == null) return;
        var group = cmbGroup.SelectedItem as string;
        var search = txtResourceSearch.Text.Trim();
        IEnumerable<OreResource> items = _oreList;
        if (!string.IsNullOrEmpty(group) && group != AllGroups)
            items = items.Where(x => string.Equals(x.GroupName, group, StringComparison.OrdinalIgnoreCase));
        if (search.Length > 0)
            items = items.Where(x => x.Display.Contains(search, StringComparison.OrdinalIgnoreCase)
                                  || x.RecordName.Contains(search, StringComparison.OrdinalIgnoreCase)
                                  || (x.NameEn?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        var prev = (cmbResource.SelectedItem as OreResource)?.Uuid;
        var list = items.ToList();
        cmbResource.ItemsSource = list;
        var keep = prev != null ? list.FirstOrDefault(x => string.Equals(x.Uuid, prev, StringComparison.OrdinalIgnoreCase)) : null;
        if (keep != null) cmbResource.SelectedItem = keep;
        else if (list.Count > 0) cmbResource.SelectedIndex = 0;
    }

    // 品質の欄の文字 → 0〜1000 の整数 (空欄は null)。読めなければ false
    private static bool TryParseQuality(string text, out int? quality)
    {
        quality = null;
        text = text.Trim();
        if (text.Length == 0) return true;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var q) || !OreService.IsValidQuality(q)) return false;
        quality = q;
        return true;
    }

    // 量の欄の文字 → 数 (桁区切りの "," は許す)。読めなければ false
    private static bool TryParseQuantity(string text, out double quantity) =>
        double.TryParse(text.Trim(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out quantity) && !double.IsNaN(quantity) && !double.IsInfinity(quantity);

    internal static string FormatQuantity(double q) => q.ToString("0.##", CultureInfo.InvariantCulture);
    private static string FormatNumber(double? v) => v?.ToString("#,0.##", CultureInfo.InvariantCulture) ?? "";

    private void AddStock_Click(object sender, RoutedEventArgs e)
    {
        if (cmbResource.SelectedItem is not OreResource res)
        {
            MessageBox.Show("資源を選んでください。", "入力エラー");
            return;
        }
        if (!TryParseQuality(txtQuality.Text, out var quality))
        {
            MessageBox.Show("品質は 0〜1000 の整数で入力してください (空欄は不明)。", "入力エラー");
            return;
        }
        if (!TryParseQuantity(txtQuantity.Text, out var qty) || qty <= 0)
        {
            MessageBox.Show("量は 0 より大きい数で入力してください。", "入力エラー");
            return;
        }
        var unit = cmbUnit.SelectedItem as string ?? OreService.UnitScu;
        var s = new OreStock { ResourceUuid = res.Uuid, ResourceRecord = res.RecordName, Quality = quality, Quantity = qty, Unit = unit, Notes = txtNotes.Text.Trim() };
        try
        {
            _service.SetCacheDir(WorkDir());
            _service.AddOrMergeStock(s);
            txtNotes.Text = "";
            txtQuantity.Text = "1";
            RefreshStock();
            txtOreStatus.Text += $" | 追加: {res.Display} 品質 {QualityText(quality)} {FormatQuantity(qty)} {unit}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存に失敗しました: {ex.Message}", "エラー");
        }
    }

    private static string QualityText(int? quality) => quality?.ToString(CultureInfo.InvariantCulture) ?? "不明";

    private void DeleteStock_Click(object sender, RoutedEventArgs e)
    {
        if (dgStock.SelectedItem is not OreStockRow row) return;
        var s = row.Model;
        if (MessageBox.Show($"「{row.ResourceDisplay}」(品質 {QualityText(s.Quality)} / {FormatQuantity(s.Quantity)} {s.Unit}) を削除しますか？", "確認", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try
        {
            _service.SetCacheDir(WorkDir());
            _service.DeleteStock(s.Id);
            RefreshStock();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"削除に失敗しました: {ex.Message}", "エラー");
        }
    }

    // 表の欄の書き換え (OreStockRow の setter から)。表の編集の確定の最中なので、DB への書き戻しと読み直しは後で行う
    private void StockQuality_Changed(OreStockRow row, string text) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => ApplyStockQuality(row, text));
    private void StockQuantity_Changed(OreStockRow row, string text) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => ApplyStockQuantity(row, text));
    private void StockNotes_Changed(OreStockRow row, string text) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => ApplyStockNotes(row, text));

    // 品質を書き換える (0〜1000 の整数か空欄)。変えた結果が同じ組 (資源・品質・単位) の別の行とぶつかれば、OreService.UpdateStock が合算する
    private void ApplyStockQuality(OreStockRow row, string text)
    {
        try
        {
            if (!TryParseQuality(text, out var quality))
                MessageBox.Show("品質は 0〜1000 の整数で入力してください (空欄は不明)。", "入力エラー");
            else if (quality != row.Model.Quality)
            {
                row.Model.Quality = quality;
                _service.SetCacheDir(WorkDir());
                _service.UpdateStock(row.Model);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存に失敗しました: {ex.Message}", "エラー");
        }
        RefreshStock();
    }

    // 量を書き換える。負の数は受け付けない。0 になるなら行を消すか確かめ、「いいえ」なら変えない
    private void ApplyStockQuantity(OreStockRow row, string text)
    {
        var s = row.Model;
        try
        {
            if (!TryParseQuantity(text, out var qty) || qty < 0)
                MessageBox.Show("量は 0 以上の数で入力してください。", "入力エラー");
            else if (qty == 0)
            {
                if (MessageBox.Show($"量が 0 になります。「{row.ResourceDisplay}」(品質 {QualityText(s.Quality)} / {s.Unit}) の行を削除しますか？", "確認", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                {
                    _service.SetCacheDir(WorkDir());
                    _service.DeleteStock(s.Id);
                }
            }
            else if (qty != s.Quantity)
            {
                s.Quantity = qty;
                _service.SetCacheDir(WorkDir());
                _service.UpdateStock(s);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存に失敗しました: {ex.Message}", "エラー");
        }
        RefreshStock();
    }

    private void ApplyStockNotes(OreStockRow row, string text)
    {
        try
        {
            row.Model.Notes = text.Trim();
            _service.SetCacheDir(WorkDir());
            _service.UpdateStock(row.Model);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存に失敗しました: {ex.Message}", "エラー");
        }
        RefreshStock();
    }

    // === 売買・採掘の記録 ===

    private void RefreshTables()
    {
        try
        {
            _service.SetCacheDir(WorkDir());
            tblTrades.SetItems(_service.LoadTrades());
            tblMining.SetItems(_service.LoadMining());
            ReloadSellProfits();
        }
        catch (Exception ex)
        {
            txtOreStatus.Text = $"記録の読み込みエラー: {ex.Message}";
        }
    }

    private static IComparable? TimeKey(string logTime) => OreService.TryParseLogTime(logTime, out var t) ? t : (logTime.Length > 0 ? logTime : null);
    private static IComparable? TextKey(string s) => s.Length > 0 ? s : null;

    private List<GridColumnSpec> TradeColumns() =>
    [
        new() { Header = "時刻", Filterable = false, Text = o => OreService.LogTimeDisplay(((CommodityTrade)o).LogTime), Key = o => TimeKey(((CommodityTrade)o).LogTime) },
        new() { Header = "種類", Text = o => ((CommodityTrade)o).IsBuy ? "購入" : "売却", Key = o => ((CommodityTrade)o).IsBuy ? "購入" : "売却" },
        new() { Header = "資源", Text = o => TradeResource((CommodityTrade)o), Key = o => TextKey(TradeResource((CommodityTrade)o)) },
        new() { Header = "量 SCU", Numeric = true, Filterable = false, Text = o => FormatNumber(((CommodityTrade)o).QuantityScu), Key = o => ((CommodityTrade)o).QuantityScu },
        new() { Header = "合計 aUEC", Numeric = true, Filterable = false, Text = o => FormatNumber(((CommodityTrade)o).TotalAuec), Key = o => ((CommodityTrade)o).TotalAuec },
        new() { Header = "単価", Numeric = true, Filterable = false, Text = o => FormatNumber(((CommodityTrade)o).UnitPrice), Key = o => ((CommodityTrade)o).UnitPrice },
        new() { Header = "店", Text = o => ((CommodityTrade)o).ShopName, Key = o => TextKey(((CommodityTrade)o).ShopName) },
        new() { Header = "結果", Text = o => TradeResult((CommodityTrade)o), Key = o => TradeResult((CommodityTrade)o) },
        new() { Header = "仕入れ", Numeric = true, Filterable = false, Text = o => FormatNumber(((CommodityTrade)o).CostAuec), Key = o => ((CommodityTrade)o).CostAuec },
        new() { Header = "利益", Numeric = true, Filterable = false, Text = o => FormatNumber(((CommodityTrade)o).ProfitAuec), Key = o => ((CommodityTrade)o).ProfitAuec },
        new() { Header = "利益率", Numeric = true, Filterable = false, Text = o => ((CommodityTrade)o).ProfitRate is { } r ? r.ToString("0.0%", CultureInfo.InvariantCulture) : "", Key = o => ((CommodityTrade)o).ProfitRate },
    ];

    private const int TradeResourceColumn = 2;    // 売買の記録の「資源」の列 (TradeColumns の順)
    private const int MiningResourceColumn = 1;   // 採掘の記録の「資源」の列 (MiningColumns の順)

    private string TradeResource(CommodityTrade t) => ResourceDisplay(t.ResourceUuid, t.ResourceRecord);
    private static string TradeResult(CommodityTrade t) => t.Succeeded ? "成功" : t.Result;

    private List<GridColumnSpec> MiningColumns() =>
    [
        new() { Header = "時刻", Filterable = false, Text = o => OreService.LogTimeDisplay(((MiningLog)o).LogTime), Key = o => TimeKey(((MiningLog)o).LogTime) },
        new() { Header = "資源", Text = o => MiningResource((MiningLog)o), Key = o => TextKey(MiningResource((MiningLog)o)) },
        new() { Header = "量", Numeric = true, Filterable = false, Text = o => FormatNumber(((MiningLog)o).Quantity), Key = o => ((MiningLog)o).Quantity },
        new() { Header = "単位", Text = o => ((MiningLog)o).Unit, Key = o => TextKey(((MiningLog)o).Unit) },
        new() { Header = "品質", Numeric = true, Text = o => ((MiningLog)o).Quality?.ToString(CultureInfo.InvariantCulture) ?? "", Key = o => ((MiningLog)o).Quality },
        new() { Header = "方法", Text = o => MethodDisplay(((MiningLog)o).Method), Key = o => MethodDisplay(((MiningLog)o).Method) },
    ];

    // 資源を引けていない採掘の行 (resource_uuid = '') はクラス名 (Harvestable_Mineral_1H_ を除く) を出す
    private string MiningResource(MiningLog m)
    {
        if (m.ResourceUuid.Length > 0 || m.ResourceRecord.Length > 0) return ResourceDisplay(m.ResourceUuid, m.ResourceRecord);
        const string prefix = "Harvestable_Mineral_1H_";
        return m.MineralClass.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? m.MineralClass[prefix.Length..] : m.MineralClass;
    }

    private static string MethodDisplay(string method) => method switch
    {
        "hand" => "手掘り",
        "ship" => "船",
        "geo" => "GEO",
        "manual" => "手入力",
        _ => method,
    };

    // 「利益を再計算」: 利益を計算し直し (RecomputeProfits)、まだ在庫に入れていない記録を在庫へ反映して (ApplyToStock)、表を作り直す
    private void Recompute_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            int sells;
            (int Trades, int Mining, double Clamped) applied;
            lock (_importLock)
            {
                _service.SetCacheDir(WorkDir());
                sells = _service.RecomputeProfits();
                applied = _service.ApplyToStock(GameLogPurchase.ImportCutoffUtc);
                ReloadSellProfits();
            }
            RefreshStock();
            RefreshTables();
            txtOreStatus.Text += $" | 利益を計算した売却 {sells} 件、在庫へ反映: 売買 {applied.Trades} 件・採掘 {applied.Mining} 件"
                + (applied.Clamped > 0 ? $" (在庫が足りず 0 で止めた量 {FormatNumber(applied.Clamped)})" : "");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"再計算に失敗しました: {ex.Message}", "エラー");
        }
    }

    // === ログの解析からの取り込み (後半で接続) ===

    // 売買の記録と採掘の記録を受け取り、主キーで二重を防いで入れ (InsertIgnore)、新しく入った行があれば利益を計算し直して在庫へ反映し、表を作り直す。
    // 新しく入った行が無くても、まだ在庫に反映していない行 (HasUnappliedRecords。前の反映が database is locked 等で失敗して残ったもの) があれば同じように反映する。
    // 採掘の ResourceUuid が空なら MineralClass から resource_types を引く。どのスレッドからでも呼べる (DB の書き込みは呼んだスレッドで 1 つずつ行い、
    // 表の作り直しは UI のスレッドへ回す)。戻り値は新しく入った (売買, 採掘) の件数
    public (int Trades, int Mining) ImportLogRecords(IReadOnlyList<OreTradeRecord> trades, IReadOnlyList<OreMiningRecord> mining)
    {
        int t = 0, m = 0;
        var applied = false;
        lock (_importLock)
        {
            var workDir = WorkDir();
            _service.SetCacheDir(workDir);
            var gamedata = Path.Combine(workDir, "gamedata_cache.db");
            if (trades.Count > 0) t = _service.InsertTradesIgnore(trades.Select(ToTrade));
            if (mining.Count > 0) m = _service.InsertMiningIgnore(mining.Select(x => ToMining(x, gamedata)));
            if (t > 0 || m > 0 || _service.HasUnappliedRecords(GameLogPurchase.ImportCutoffUtc))
            {
                _service.RecomputeProfits();
                // 在庫を変えるのは GameLogPurchase.ImportCutoffUtc 以降の行だけ (それより前の記録は反映済みにするだけ)
                _service.ApplyToStock(GameLogPurchase.ImportCutoffUtc);
                ReloadSellProfits();
                applied = true;
            }
        }
        if (applied)
        {
            if (Dispatcher.CheckAccess()) RefreshAfterImport();
            else Dispatcher.BeginInvoke(RefreshAfterImport);
        }
        return (t, m);
    }

    // 取引の結果 (失敗) が後から結び付いたとき (GameLogPanel.TradeResultUpdater): 同じ鍵の行の result を書き換え (在庫へ反映済みなら戻す)、
    // 利益を計算し直して表を作り直す。行が無ければ (まだ入れていない) false
    public bool UpdateTradeResult(OreTradeRecord r)
    {
        bool updated;
        lock (_importLock)
        {
            _service.SetCacheDir(WorkDir());
            updated = _service.UpdateTradeResult(r.LogTime, r.PlayerId, r.ShopId, r.KioskId, r.Kind, r.ResourceUuid, r.QuantityRaw, r.Result ?? "");
            if (updated)
            {
                _service.RecomputeProfits();
                ReloadSellProfits();
            }
        }
        if (updated)
        {
            if (Dispatcher.CheckAccess()) RefreshAfterImport();
            else Dispatcher.BeginInvoke(RefreshAfterImport);
        }
        return updated;
    }

    // 売却の行の利益 (鍵 → (利益, 利益率))。GameLogPanel の売却の行に添える。計算前の行は null。
    // 利益を計算し直すたびに読み直す (_profitLock で守る。GameLogPanel は UI のスレッドから引く)
    private readonly object _profitLock = new();
    private Dictionary<string, (double Profit, double? Rate)> _sellProfits = new(StringComparer.Ordinal);
    private bool _profitsLoaded;   // 一度も読んでいなければ、初めて引かれたときに DB から読む (［鉱石］を表示していなくても売却の行に添えられるように)

    public (double Profit, double? Rate)? TryGetSellProfit(string key)
    {
        if (!_profitsLoaded)
        {
            lock (_importLock)
            {
                _service.SetCacheDir(WorkDir());
                ReloadSellProfits();
            }
        }
        lock (_profitLock) return _sellProfits.TryGetValue(key, out var v) ? v : null;
    }

    private void ReloadSellProfits()
    {
        Dictionary<string, (double Profit, double? Rate)> dict;
        try { dict = _service.LoadSellProfits(); }
        catch { return; }
        lock (_profitLock)
        {
            _sellProfits = dict;
            _profitsLoaded = true;
        }
    }

    private void RefreshAfterImport()
    {
        if (!_loadedOnce) return;
        RefreshStock();
        RefreshTables();
    }

    private static CommodityTrade ToTrade(OreTradeRecord r) => new()
    {
        LogTime = r.LogTime, PlayerId = r.PlayerId ?? "", ShopId = r.ShopId ?? "", KioskId = r.KioskId ?? "", ShopName = r.ShopName ?? "",
        Kind = r.Kind, ResourceUuid = r.ResourceUuid ?? "", ResourceRecord = r.ResourceRecord ?? "", QuantityScu = r.QuantityScu,
        QuantityRaw = r.QuantityRaw ?? "", TotalAuec = r.TotalAuec, TransactionMode = r.TransactionMode ?? "", Result = r.Result ?? "", Source = "log",
    };

    private MiningLog ToMining(OreMiningRecord r, string gamedataDbPath)
    {
        var uuid = r.ResourceUuid ?? "";
        var record = r.ResourceRecord ?? "";
        if (uuid.Length == 0 && !string.IsNullOrEmpty(r.MineralClass))
        {
            if (!_mineralCache.TryGetValue(r.MineralClass, out var found))
                _mineralCache[r.MineralClass] = found = OreService.ResolveMineralResource(gamedataDbPath, r.MineralClass);
            if (found is { } f) (uuid, record) = f;
        }
        return new MiningLog
        {
            LogTime = r.LogTime, PlayerName = r.PlayerName ?? "", ResourceUuid = uuid, ResourceRecord = record, MineralClass = r.MineralClass ?? "",
            EntityId = r.EntityId ?? "", Quantity = r.Quantity, Unit = string.IsNullOrEmpty(r.Unit) ? OreService.UnitPiece : r.Unit,
            Quality = r.Quality, Method = string.IsNullOrEmpty(r.Method) ? "hand" : r.Method, Source = r.Method == "manual" ? "manual" : "log",
        };
    }
}

// 在庫 (dgStock) の表示行。OreStock を包み、品質・量・メモの書き換えを OnXxxChanged 経由で OrePanel へ知らせる
// (Model はここでは書き換えず、受け手が確かめてから DB へ書き戻して読み直す。MyComponentRow と同じ流儀)
public sealed class OreStockRow
{
    public OreStock Model { get; }
    public Action<OreStockRow, string>? OnQualityChanged { get; set; }
    public Action<OreStockRow, string>? OnQuantityChanged { get; set; }
    public Action<OreStockRow, string>? OnNotesChanged { get; set; }

    public OreStockRow(OreStock model) => Model = model;

    public int Id => Model.Id;
    public string ResourceDisplay { get; set; } = "";
    public string GroupName { get; set; } = "";
    public string Unit => Model.Unit;
    public string UpdatedAt => Model.UpdatedAt;

    public string QualityText
    {
        get => Model.Quality?.ToString(CultureInfo.InvariantCulture) ?? "";
        set
        {
            if (value == QualityText) return;
            OnQualityChanged?.Invoke(this, value);
        }
    }

    public string QuantityText
    {
        get => OrePanel.FormatQuantity(Model.Quantity);
        set
        {
            if (value == QuantityText) return;
            OnQuantityChanged?.Invoke(this, value);
        }
    }

    public string Notes
    {
        get => Model.Notes;
        set
        {
            if (value == Model.Notes) return;
            OnNotesChanged?.Invoke(this, value);
        }
    }
}

// ［鉱石］タブのシグネチャー: スキャナーに出た数字 N から、鉱石の種類・岩の数・格を出し、選んだ場所で出る割合 (ore_locations の chance) を添える。
// 値は gamedata_cache.db の ore_signatures (岩 1 個の RS と群れ。GameDataExtractor.FindOreLocations が作る)。
//   ・船の岩: N = RS × k (k は 1 から、その岩の群れの最大の数 (cluster_max。群れのデータが無ければ 1) まで) にちょうど一致するものを全部出す。
//     「数字 = 1 個の値 × 岩の数」は推定 (データ・一次資料に式は無い)
//   ・N が 4000 の倍数なら ROC の岩 × (N ÷ 4000)、3000 の倍数なら手掘りの岩 × (N ÷ 3000) (この掘り方の岩はみな同じ値なので種類は分からない)。
//     その場所のその掘り方の岩と出る割合を出す
//   ・船の岩のちょうどの一致も ROC・手掘りの一致も無ければ「一致なし」とし、船の岩 × k の近い順に 3 件を「近いもの」として出す
public static class OreSignatureCalc
{
    // ROC の岩・手掘りの岩の値 (調べた Data.p4k では、この掘り方の岩はすべてこの値)
    public const double RocSignature = 4000, HandSignature = 3000;
    private const double Eps = 1e-6;

    // 岩の種類 1 つ (掘り方・名前・RS・格 (英語。無ければ "")・岩の数の最大)
    public sealed record Rock(string Method, string Name, double Signature, string Grade, int MaxCount);

    // 場所 1 つ (ore_locations の place と system)。None は［場所を選ばない］
    public sealed record Place(string Name, string System)
    {
        public static readonly Place None = new("", "");
        public bool IsNone => Name.Length == 0;
        public string Display => IsNone ? "（場所を選ばない）" : System.Length > 0 ? $"{Name}（{System}）" : Name;
        public override string ToString() => Display;
    }

    // ore_locations の 1 行のうち使うもの
    public sealed record Location(string Place, string System, string Method, string Rock, string Preset, double Chance);

    public sealed class Data
    {
        public static readonly Data Empty = new([], [], []);
        public Data(List<Rock> rocks, List<Location> locations, List<Place> places) { Rocks = rocks; Locations = locations; Places = places; }
        public List<Rock> Rocks { get; }
        public List<Location> Locations { get; }
        public List<Place> Places { get; }
        public bool HasSignatures => Rocks.Count > 0;
    }

    // 結果の 1 行 (区分・鉱石・岩の数・格・この場所で出る割合)
    public sealed record Row(string Kind, string Ore, string Count, string Grade, string Chance);

    public sealed record Result(List<string> Messages, List<Row> Rows);

    // gamedata_cache.db から ore_signatures と ore_locations を読む。表が無い・DB が無ければ空
    public static Data Load(string gamedataDbPath)
    {
        if (!File.Exists(gamedataDbPath)) return Data.Empty;
        using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={gamedataDbPath};Mode=ReadOnly;Pooling=False");
        c.Open();
        bool Has(string table)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @t";
            cmd.Parameters.AddWithValue("@t", table);
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0;
        }
        var raw = new List<(string Method, string Rock, double Sig, string Grade, int? Max)>();
        if (Has(GameDataExtractor.OreSignaturesTable))
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT method, rock, signature, grade, cluster_max FROM ore_signatures";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                raw.Add((r.GetString(0), r.GetString(1), r.GetDouble(2), r.IsDBNull(3) ? "" : r.GetString(3), r.IsDBNull(4) ? null : (int?)r.GetInt32(4)));
        }
        // (掘り方, 岩, RS) ごとにまとめる: 格は空でない最初のもの、岩の数の最大は cluster_max の最大 (群れのデータの無い行は 1)
        var rocks = raw.GroupBy(x => (x.Method, x.Rock, x.Sig))
            .Select(g => new Rock(g.Key.Method, g.Key.Rock, g.Key.Sig,
                g.Select(x => x.Grade).FirstOrDefault(x => x.Length > 0) ?? "",
                Math.Max(1, g.Max(x => x.Max ?? 1))))
            .OrderBy(x => x.Method, StringComparer.Ordinal).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var locs = new List<Location>();
        if (Has(GameDataExtractor.OreLocationsTable))
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT place, system, method, rock, preset, chance FROM ore_locations";
            using var r = cmd.ExecuteReader();
            while (r.Read()) locs.Add(new Location(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetDouble(5)));
        }
        static int SystemOrder(string s) => Array.FindIndex(MapPanel.SystemNames, n => n.Equals(s, StringComparison.OrdinalIgnoreCase)) is var i && i >= 0 ? i : 99;
        var places = locs.Select(x => new Place(x.Place, x.System)).Distinct()
            .OrderBy(x => SystemOrder(x.System)).ThenBy(x => x.System, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new Data(rocks, locs, places);
    }

    // 格 (英語) → 画面の文字: translations.db の mineabletype_rarity_<格> の日本語 (ゲームの訳)。無ければ英語のまま (返す辞書に入れない)
    public static Dictionary<string, string> LoadGradeLabels(string translationsDbPath)
    {
        string[] grades = ["Common", "Uncommon", "Rare", "Epic", "Legendary"];
        var keys = new HashSet<string>(grades.Select(g => "mineabletype_rarity_" + g.ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);
        var loc = OreService.LoadTranslations(translationsDbPath, keys);
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in grades)
            if (loc.TryGetValue("mineabletype_rarity_" + g.ToLowerInvariant(), out var t) && OreService.JapaneseText(t.Ja) is { } ja) d[g] = ja;
        return d;
    }

    // 近くの天体の名前 body と星系 system に合う場所 (名前が同じ (大文字小文字を区別しない)。星系も同じものを優先)。無ければ null
    public static Place? MatchPlace(IEnumerable<Place> places, string? body, string? system)
    {
        if (string.IsNullOrEmpty(body)) return null;
        var same = places.Where(p => p.Name.Equals(body, StringComparison.OrdinalIgnoreCase)).ToList();
        return same.FirstOrDefault(p => system != null && p.System.Equals(system, StringComparison.OrdinalIgnoreCase)) ?? same.FirstOrDefault();
    }

    // その場所でその掘り方・岩が出る割合 (ore_locations の chance)。場所に 2 つ以上の preset があって値が違えば「a% / b%」(大きい順)。
    // 場所を選んでいなければ「（場所を選んでいない）」、その場所に無ければ「この場所には無い」
    public static string ChanceText(Data data, Place? place, string method, string rock)
    {
        if (place == null || place.IsNone) return "（場所を選んでいない）";
        var values = data.Locations.Where(l => l.Place == place.Name && l.System == place.System && l.Method == method && l.Rock == rock)
            .Select(l => Math.Round(l.Chance, 1)).Distinct().OrderByDescending(v => v).ToList();
        return values.Count == 0 ? "この場所には無い" : string.Join(" / ", values.Select(v => v.ToString("0.0", CultureInfo.InvariantCulture) + "%"));
    }

    public static Result Compute(Data data, double n, Place? place, Func<string, string> gradeLabel)
    {
        var messages = new List<string>();
        var rows = new List<Row>();
        string Grade(string g) => g.Length == 0 ? "—" : gradeLabel(g);
        var ship = data.Rocks.Where(r => r.Method == GameDataExtractor.OreMethodShip).ToList();

        // 船の岩: N = RS × k のちょうどの一致
        var exact = new List<(Rock Rock, int K)>();
        foreach (var r in ship)
            for (int k = 1; k <= r.MaxCount; k++)
                if (Math.Abs(r.Signature * k - n) < Eps) exact.Add((r, k));
        foreach (var (r, k) in exact.OrderBy(x => x.K).ThenBy(x => x.Rock.Name, StringComparer.OrdinalIgnoreCase))
            rows.Add(new Row("一致", r.Name, k.ToString(CultureInfo.InvariantCulture), Grade(r.Grade), ChanceText(data, place, r.Method, r.Name)));
        if (exact.Count > 0) messages.Add($"船の岩: {exact.Count} 件一致");

        // ROC・手掘りの岩: 値の倍数なら、その掘り方の岩 × k (種類は分からない)
        var groupMatched = false;
        foreach (var (method, sig, label) in new[] { (GameDataExtractor.OreMethodRoc, RocSignature, "ROC の岩"), (GameDataExtractor.OreMethodHand, HandSignature, "手掘りの岩") })
        {
            var kd = n / sig;
            var k = Math.Round(kd);
            if (k < 1 || Math.Abs(sig * k - n) >= Eps) continue;   // 船の岩と同じく、差そのもので比べる
            groupMatched = true;
            var kText = k.ToString("0", CultureInfo.InvariantCulture);
            messages.Add($"{label} × {kText}（数字では種類が分からない）");
            var kind = $"{label} × {kText}";
            if (place != null && !place.IsNone)
            {
                var here = data.Locations.Where(l => l.Place == place.Name && l.System == place.System && l.Method == method)
                    .Select(l => l.Rock).Distinct().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                if (here.Count == 0) rows.Add(new Row(kind, $"（{label}はこの場所に無い）", kText, "—", "この場所には無い"));
                foreach (var rock in here)
                {
                    var grade = data.Rocks.FirstOrDefault(x => x.Method == method && x.Name == rock)?.Grade ?? "";
                    rows.Add(new Row(kind, rock, kText, Grade(grade), ChanceText(data, place, method, rock)));
                }
            }
            else
            {
                foreach (var r in data.Rocks.Where(x => x.Method == method).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                    rows.Add(new Row(kind, r.Name, kText, Grade(r.Grade), ChanceText(data, place, method, r.Name)));
            }
        }

        // どれにも一致しなければ「一致なし」と、船の岩 × k の近い順に 3 件
        if (exact.Count == 0 && !groupMatched)
        {
            messages.Add("一致なし");
            var near = ship.SelectMany(r => Enumerable.Range(1, r.MaxCount).Select(k => (Rock: r, K: k, Diff: r.Signature * k - n)))
                .OrderBy(x => Math.Abs(x.Diff)).ThenBy(x => x.Rock.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.K).Take(3);
            foreach (var (r, k, diff) in near)
            {
                var d = diff.ToString("+#,0.##;-#,0.##;0", CultureInfo.InvariantCulture);
                rows.Add(new Row($"近いもの（差 {d}）", r.Name, k.ToString(CultureInfo.InvariantCulture), Grade(r.Grade), ChanceText(data, place, r.Method, r.Name)));
            }
        }
        return new Result(messages, rows);
    }
}
