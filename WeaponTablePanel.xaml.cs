using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace StarCitizenJapaneseTextCreater;

// 武器の表 (［船舶戦闘］［個人戦闘］タブ)。武器をすべて 1 つの表に出す (個人: 個人武器の全種類。医療ガンは除く / 船: 船の武器とミサイル・魚雷 (ロケット・ロケットポッドを含む))。
// 値は比べるツールと同じ計算 (GameDataQueryService.GetPersonalWeaponTable / GetShipWeaponTable)。
// まずローカルの DB (gamedata_cache.db。読み取り専用) の値で出し、店の最安値の UEX の値段は裏で取ってから埋める (チャットと同じ取得・キャッシュ)
public partial class WeaponTablePanel : UserControl
{
    // true なら船の武器、false なら個人武器
    public bool IsShip { get; set; }

    private bool _initialized;
    // 品目名が作り直された (GameDataExtractor.ItemNamesChanged) ので、次に表示されたときに読み直す
    private bool _namesStale;
    private int _loadVersion;

    public WeaponTablePanel()
    {
        InitializeComponent();
        table.VisibleRowsChanged += UpdateNotes;
        // 名前のダブルクリック → ［詳細］タブにその品目を出す
        table.NameDoubleClicked += o => { if (o is WeaponTableRow r) DetailPanel.Request(r.Record, r.Name); };
        // 品目名が作り直されたら、次に表示されたとき (Loaded) に読み直す
        GameDataExtractor.ItemNamesChanged += () => _namesStale = true;
        Loaded += async (_, _) =>
        {
            if (_initialized)
            {
                if (!_namesStale) return;
                _namesStale = false;
                try { await LoadAsync(); }
                catch (Exception ex) { ShowMessage($"読み込めませんでした: {ex.Message}"); }
                return;
            }
            _initialized = true;
            // 表の部品の例外でもアプリを落とさず、1 行の知らせにする
            try
            {
                table.SetColumns(IsShip ? ShipColumns() : PersonalColumns());
                if (!IsShip) AddArmorSelectors();
                await LoadAsync();
            }
            catch (Exception ex) { ShowMessage($"読み込めませんでした: {ex.Message}"); }
        };
    }

    // gamedata_cache.db の場所 (MainWindow.InitGameDataExtractor と同じ作業ディレクトリ)
    internal static string DbPath()
    {
        var workDir = App.Config.WorkingDirectory;
        if (string.IsNullOrEmpty(workDir)) workDir = AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(workDir, "gamedata_cache.db");
    }

    private static GridColumnSpec Text(string header, Func<WeaponTableRow, string> f) =>
        new() { Header = header, Text = o => f((WeaponTableRow)o), Key = o => f((WeaponTableRow)o) is { Length: > 0 } s ? s : null };

    // 数値の列 (1発のダメージより右の列) には絞り込みのボタン (Select) を出さない (並べ替えはできる)
    private static GridColumnSpec Number(string header, Func<WeaponTableRow, double?> f, string format) =>
        new() { Header = header, Numeric = true, Filterable = false, Text = o => f((WeaponTableRow)o)?.ToString(format) ?? "", Key = o => f((WeaponTableRow)o) };

    // 「店の最安値」の列。セルのツールチップに売り場 (星系 / 場所 / 店名 / 値段) を安い順に出す (WeaponTableRow.PriceTooltip)
    private static GridColumnSpec PriceColumn() =>
        new()
        {
            Header = "店の最安値 (aUEC)", Numeric = true, Filterable = false,
            Text = o => ((WeaponTableRow)o).MinPrice?.ToString("#,0") ?? "", Key = o => ((WeaponTableRow)o).MinPrice,
            Tooltip = o => ((WeaponTableRow)o).PriceTooltip,
        };

    private static GridColumnSpec NameColumn() =>
        new() { Header = "名前", Text = o => ((WeaponTableRow)o).DisplayName, Key = o => ((WeaponTableRow)o).Name };

    // 船の武器の列: 名前、サイズ、種類、ダメージの種類、弾 (使う / 使わない)、追尾 (ミサイル・魚雷が追尾する信号)、メーカー、1発のダメージ、
    // Damage/Alpha (1発のダメージ × 1回の弾数)、バーストDPS、平均DPS、弾速、店の最安値。ミサイル・魚雷に無い値 (DPS 等) は空
    private static List<GridColumnSpec> ShipColumns() =>
    [
        NameColumn(),
        new() { Header = "サイズ", Numeric = true, Text = o => ((WeaponTableRow)o).Size > 0 ? $"S{((WeaponTableRow)o).Size}" : "", Key = o => ((WeaponTableRow)o).Size > 0 ? ((WeaponTableRow)o).Size : null },
        Text("種類", r => r.Kind),
        Text("ダメージの種類", r => r.DamageTypes),
        Text("弾", r => r.Ammo),
        Text("追尾", r => r.Tracking),
        Text("メーカー", r => r.Manufacturer),
        Number("1発のダメージ", r => r.Damage, "#,0.##"),
        Number("Damage/Alpha", r => r.Alpha, "#,0.##"),
        Number("バーストDPS", r => r.MaxDps, "#,0.##"),
        Number("平均DPS", r => r.AvgDps, "#,0.##"),
        Number("弾速 (m/s)", r => r.Speed, "#,0.#"),
        PriceColumn(),
    ];

    // ダウンまでの発数の列 (［個人戦闘］)。armor は 'H' ヘビー / 'M' ミディアム / 'L' ライト。見出しは「ダウン(H/body)」「ダウン(M/head)」など
    internal static GridColumnSpec DownColumn(char armor, bool head) =>
        Number($"ダウン({armor}/{(head ? "head" : "body")})", r => r.DownShots(armor, head), "0");

    // ［個人戦闘］のダウンまでの発数の列の位置 (PersonalColumns の並び)
    internal const int TorsoDownColumn = 10, HeadDownColumn = 11;

    // 表の上 (検索欄の並び) に「胴: [H|M|L]」「頭: [H|M|L]」の選択を置く (初期は H)。選び直すとその列の値と見出しを差し替える
    // (並べ替え・他の列の絞り込みは残し、その列の絞り込みは値が変わるので解除する)
    private void AddArmorSelectors()
    {
        foreach (var (label, column, head) in new[] { ("胴:", TorsoDownColumn, false), ("頭:", HeadDownColumn, true) })
        {
            var combo = new ComboBox { ItemsSource = new[] { "H", "M", "L" }, SelectedIndex = 0, Margin = new Thickness(4, 0, 8, 0), MinWidth = 44 };
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is string s && s.Length == 1) table.ReplaceColumn(column, DownColumn(s[0], head));
            };
            table.AddToolbarItem(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
            table.AddToolbarItem(combo);
        }
    }

    // 個人武器の列: 名前、種類、メーカー、ダメージの種類、1発のダメージ、1回の弾数、バーストDPS、平均DPS、弾速、装弾数、ダウンまで 胴・頭 (初期はヘビー)、店の最安値
    internal static List<GridColumnSpec> PersonalColumns() =>
    [
        NameColumn(),
        Text("種類", r => r.Kind),
        Text("メーカー", r => r.Manufacturer),
        Text("ダメージの種類", r => r.DamageTypes),
        Number("1発のダメージ", r => r.Damage, "#,0.##"),
        Number("1回の弾数", r => r.Pellets, "0"),
        Number("バーストDPS", r => r.MaxDps, "#,0.##"),
        Number("平均DPS", r => r.AvgDps, "#,0.##"),
        Number("弾速 (m/s)", r => r.Speed, "#,0.#"),
        Number("装弾数", r => r.Magazine, "0"),
        DownColumn('H', false),
        DownColumn('H', true),
        PriceColumn(),
    ];

    private async Task LoadAsync()
    {
        var version = ++_loadVersion;
        ShowMessage("読み込み中...");
        var dbPath = DbPath();
        List<WeaponTableRow>? rows;
        string? error = null;
        try
        {
            rows = await Task.Run(() =>
            {
                if (!File.Exists(dbPath)) return null;
                // 読み取り専用で開く (DB に書き込まない。構築中・VACUUM 中でも書き込みのロックを取らない)
                using var q = GameDataQueryService.OpenReadOnly(dbPath);
                if (!q.HasItemIndex()) return null;
                return IsShip ? q.GetShipWeaponTable() : q.GetPersonalWeaponTable();
            });
        }
        catch (Exception ex)
        {
            rows = new List<WeaponTableRow>();
            error = ex.Message;
        }
        if (version != _loadVersion) return;

        if (rows == null) { ShowMessage("インデックスが未構築です (設定タブの［インデックス構築］)"); return; }
        if (error != null) { ShowMessage($"読み込めませんでした: {error}"); return; }
        if (rows.Count == 0) { ShowMessage("データがありません"); return; }

        ShowMessage(null);
        table.SetItems(rows);

        // 店の最安値: まず保存データ (cstone) で出し、UEX の値段は裏で取ってから埋める (チャットと同じ取得・キャッシュを共用)
        var ids = IsShip ? GameDataQueryService.ShipWeaponTableUexIds
            : GameDataQueryService.PersonalWeaponTableCategories.SelectMany(c => c.UexCategoryIds ?? []).Distinct().ToArray();
        if (ids.Length > 0) await FillUexPricesAsync(ids, dbPath, version);
    }

    private async Task FillUexPricesAsync(int[] ids, string dbPath, int version)
    {
        try
        {
            var uex = await ChatService.GetSharedUexItemPricesAsync(ids);
            if (uex.Count == 0 || version != _loadVersion) return;
            var rows = await Task.Run(() =>
            {
                using var q = GameDataQueryService.OpenReadOnly(dbPath);
                return IsShip ? q.GetShipWeaponTable(uex) : q.GetPersonalWeaponTable(uex);
            });
            if (version != _loadVersion || rows.Count == 0) return;
            table.SetItems(rows);   // 絞り込みと並べ替えはそのまま残る
        }
        catch { }   // UEX が取れなくても保存データの表のまま
    }

    // 表に出している行に注1・注2 の印があるときだけ、表の下に説明を出す
    private void UpdateNotes(IReadOnlyList<object> visible)
    {
        var rows = visible.OfType<WeaponTableRow>().ToList();
        var notes = new List<string>();
        if (rows.Any(r => r.ChargeNote)) notes.Add(GameDataQueryService.DpsNoteChargeText);
        if (rows.Any(r => r.MixedRateNote)) notes.Add(GameDataQueryService.DpsNoteMixedText);
        txtNotes.Text = string.Join(Environment.NewLine, notes);
        txtNotes.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowMessage(string? text)
    {
        txtMessage.Text = text ?? "";
        txtMessage.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
}
