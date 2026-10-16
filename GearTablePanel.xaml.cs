using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace StarCitizenJapaneseTextCreater;

// 装備の表 (［船舶装備］［個人装備］タブ)。装備をすべて 1 つの表に出す。
//   船: パワープラント・クーラー・シールド・量子ドライブ・レーダー (GameDataQueryService.GetShipGearTable)
//   個人: アーマー・服・道具・医療用具・武器の付属品・ナイフ・手榴弾・設置型の爆発物 (GetPersonalGearTable)
// 値は比べるツールと同じ計算。まずローカルの DB (gamedata_cache.db。読み取り専用) の値で出し、店の最安値の UEX の値段は裏で取ってから埋める
// (［船舶戦闘］［個人戦闘］の WeaponTablePanel と同じ流れ)
public partial class GearTablePanel : UserControl
{
    // true なら船の部品、false なら個人装備
    public bool IsShip { get; set; }

    private bool _initialized;
    // 品目名が作り直された (GameDataExtractor.ItemNamesChanged) ので、次に表示されたときに読み直す
    private bool _namesStale;
    private int _loadVersion;

    public GearTablePanel()
    {
        InitializeComponent();
        // 名前のダブルクリック → ［詳細］タブにその品目を出す
        table.NameDoubleClicked += o => { if (o is GearTableRow r) DetailPanel.Request(r.Record, r.Name); };
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
                await LoadAsync();
            }
            catch (Exception ex) { ShowMessage($"読み込めませんでした: {ex.Message}"); }
        };
    }

    private static string DbPath()
    {
        var workDir = App.Config.WorkingDirectory;
        if (string.IsNullOrEmpty(workDir)) workDir = AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(workDir, "gamedata_cache.db");
    }

    private static GridColumnSpec Text(string header, Func<GearTableRow, string> f, bool filterable = true) =>
        new() { Header = header, Filterable = filterable, Text = o => f((GearTableRow)o), Key = o => f((GearTableRow)o) is { Length: > 0 } s ? s : null };

    // 数値の列には絞り込みのボタン (Select) を出さない (並べ替えはできる)
    private static GridColumnSpec Number(string header, string key, string format) =>
        new() { Header = header, Numeric = true, Filterable = false, Text = o => ((GearTableRow)o).N(key)?.ToString(format) ?? "", Key = o => ((GearTableRow)o).N(key) };

    private static GridColumnSpec NameColumn() =>
        new() { Header = "名前", Filterable = false, Text = o => ((GearTableRow)o).Name, Key = o => ((GearTableRow)o).Name };

    private static GridColumnSpec SizeColumn() =>
        new() { Header = "サイズ", Numeric = true, Text = o => ((GearTableRow)o).Size > 0 ? $"S{((GearTableRow)o).Size}" : "", Key = o => ((GearTableRow)o).Size > 0 ? ((GearTableRow)o).Size : null };

    // 「店の最安値」の列。セルのツールチップに売り場 (星系 / 場所 / 店名 / 値段) を安い順に出す
    private static GridColumnSpec PriceColumn() =>
        new()
        {
            Header = "店の最安値 (aUEC)", Numeric = true, Filterable = false,
            Text = o => ((GearTableRow)o).MinPrice?.ToString("#,0") ?? "", Key = o => ((GearTableRow)o).MinPrice,
            Tooltip = o => ((GearTableRow)o).PriceTooltip,
        };

    // 船の部品の列: 名前、種類、サイズ、グレード、クラス、メーカー、出力、冷却、シールド容量、シールド回復、量子ドライブの速度・スプール、レーダーの感度、店の最安値。
    // 種類ごとに無い値は空 (例 パワープラントの冷却)
    private static List<GridColumnSpec> ShipColumns() =>
    [
        NameColumn(),
        Text("種類", r => r.Kind),
        SizeColumn(),
        Text("グレード", r => r.Grade),
        Text("クラス", r => r.Class),
        Text("メーカー", r => r.Manufacturer),
        Number("出力", "出力", "#,0.##"),
        Number("冷却", "冷却", "#,0.##"),
        Number("シールド HP", "シールド容量", "#,0"),
        Number("シールド回復 (毎秒)", "回復", "#,0.##"),
        Number("量子速度 (km/秒)", "速度", "#,0"),
        Number("スプール (秒)", "スプール", "0.##"),
        Number("感度", "感度", "0.##"),
        PriceColumn(),
    ];

    // 個人装備の列: 名前、種類、部位、重さ、メーカー、ダメージ軽減 (%)、耐えられる温度、放射線防護、所持容量、対応 (バックパック/コア)、
    // 性能 (説明文。アーマー・服以外)、店の最安値
    private static List<GridColumnSpec> PersonalColumns() =>
    [
        NameColumn(),
        Text("種類", r => r.Kind),
        Text("部位", r => r.Part),
        Text("重さ", r => r.Weight),
        Text("メーカー", r => r.Manufacturer),
        Number("ダメージ軽減 (%)", "ダメージ軽減", "0.##"),
        // 温度は文字のまま出し、並べ替えは温度の幅 (最高 - 最低) で行う
        new() { Header = "耐えられる温度", Filterable = false, Text = o => ((GearTableRow)o).T("耐えられる温度"), Key = o => ((GearTableRow)o).N("耐えられる温度の幅") },
        Number("放射線防護", "放射線防護", "#,0.##"),
        Number("所持容量", "所持容量", "#,0.##"),
        Text("対応 (バックパック/コア)", r => r.T("対応 (バックパック/コア)")),
        Text("性能 (説明文)", r => r.T("性能 (説明文)"), filterable: false),
        PriceColumn(),
    ];

    private async Task LoadAsync()
    {
        var version = ++_loadVersion;
        ShowMessage("読み込み中...");
        var dbPath = DbPath();
        List<GearTableRow>? rows;
        string? error = null;
        try
        {
            rows = await Task.Run(() =>
            {
                if (!File.Exists(dbPath)) return null;
                using var q = GameDataQueryService.OpenReadOnly(dbPath);
                if (!q.HasItemIndex()) return null;
                return IsShip ? q.GetShipGearTable() : q.GetPersonalGearTable();
            });
        }
        catch (Exception ex)
        {
            rows = new List<GearTableRow>();
            error = ex.Message;
        }
        if (version != _loadVersion) return;

        if (rows == null) { ShowMessage("インデックスが未構築です (設定タブの［インデックス構築］)"); return; }
        if (error != null) { ShowMessage($"読み込めませんでした: {error}"); return; }
        if (rows.Count == 0) { ShowMessage("データがありません"); return; }

        ShowMessage(null);
        table.SetItems(rows);

        var ids = (IsShip ? GameDataQueryService.ShipGearTableCategories : GameDataQueryService.PersonalGearTableCategories)
            .SelectMany(c => c.UexCategoryIds ?? []).Distinct().ToArray();
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
                return IsShip ? q.GetShipGearTable(uex) : q.GetPersonalGearTable(uex);
            });
            if (version != _loadVersion || rows.Count == 0) return;
            table.SetItems(rows);   // 絞り込みと並べ替えはそのまま残る
        }
        catch { }   // UEX が取れなくても保存データの表のまま
    }

    private void ShowMessage(string? text)
    {
        txtMessage.Text = text ?? "";
        txtMessage.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
}
