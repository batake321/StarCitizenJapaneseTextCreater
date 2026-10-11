using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace StarCitizenJapaneseTextCreater;

// 武器の表 (［船舶戦闘］［個人戦闘］タブ)。武器をすべて 1 つの表に出す (個人: 個人武器の全種類。医療ガンは除く / 船: ミサイル・魚雷以外の船の武器)。
// 値は比べるツールと同じ計算 (GameDataQueryService.GetPersonalWeaponTable / GetShipWeaponTable)。
// まずローカルの DB (gamedata_cache.db。読み取り専用) の値で出し、店の最安値の UEX の値段は裏で取ってから埋める (チャットと同じ取得・キャッシュ)
public partial class WeaponTablePanel : UserControl
{
    // true なら船の武器、false なら個人武器
    public bool IsShip { get; set; }

    private bool _initialized;
    private int _loadVersion;

    public WeaponTablePanel()
    {
        InitializeComponent();
        table.VisibleRowsChanged += UpdateNotes;
        Loaded += async (_, _) =>
        {
            if (_initialized) return;
            _initialized = true;
            table.SetColumns(IsShip ? ShipColumns() : PersonalColumns());
            await LoadAsync();
        };
    }

    // gamedata_cache.db の場所 (MainWindow.InitGameDataExtractor と同じ作業ディレクトリ)
    private static string DbPath()
    {
        var workDir = App.Config.WorkingDirectory;
        if (string.IsNullOrEmpty(workDir)) workDir = AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(workDir, "gamedata_cache.db");
    }

    private static GridColumnSpec Text(string header, Func<WeaponTableRow, string> f) =>
        new() { Header = header, Text = o => f((WeaponTableRow)o), Key = o => f((WeaponTableRow)o) is { Length: > 0 } s ? s : null };

    private static GridColumnSpec Number(string header, Func<WeaponTableRow, double?> f, string format) =>
        new() { Header = header, Numeric = true, Text = o => f((WeaponTableRow)o)?.ToString(format) ?? "", Key = o => f((WeaponTableRow)o) };

    private static GridColumnSpec NameColumn() =>
        new() { Header = "名前", Text = o => ((WeaponTableRow)o).DisplayName, Key = o => ((WeaponTableRow)o).Name };

    // 船の武器の列: 名前、サイズ、種類、弾薬/エネルギー、メーカー、1発のダメージ、最大DPS、平均DPS、弾速、店の最安値
    private static List<GridColumnSpec> ShipColumns() =>
    [
        NameColumn(),
        new() { Header = "サイズ", Numeric = true, Text = o => ((WeaponTableRow)o).Size > 0 ? $"S{((WeaponTableRow)o).Size}" : "", Key = o => ((WeaponTableRow)o).Size > 0 ? ((WeaponTableRow)o).Size : null },
        Text("種類", r => r.Kind),
        Text("弾薬/エネルギー", r => r.Ammo),
        Text("メーカー", r => r.Manufacturer),
        Number("1発のダメージ", r => r.Damage, "#,0.##"),
        Number("最大DPS", r => r.MaxDps, "#,0.##"),
        Number("平均DPS", r => r.AvgDps, "#,0.##"),
        Number("弾速 (m/s)", r => r.Speed, "#,0.#"),
        Number("店の最安値 (aUEC)", r => r.MinPrice, "#,0"),
    ];

    // 個人武器の列: 名前、種類、メーカー、ダメージの種類、1発のダメージ、1回の弾数、最大DPS、平均DPS、弾速、装弾数、ヘビー相手 胴・頭、店の最安値
    private static List<GridColumnSpec> PersonalColumns() =>
    [
        NameColumn(),
        Text("種類", r => r.Kind),
        Text("メーカー", r => r.Manufacturer),
        Text("ダメージの種類", r => r.DamageTypes),
        Number("1発のダメージ", r => r.Damage, "#,0.##"),
        Number("1回の弾数", r => r.Pellets, "0"),
        Number("最大DPS", r => r.MaxDps, "#,0.##"),
        Number("平均DPS", r => r.AvgDps, "#,0.##"),
        Number("弾速 (m/s)", r => r.Speed, "#,0.#"),
        Number("装弾数", r => r.Magazine, "0"),
        Number("ヘビー相手 胴 (ダウンまで)", r => r.HeavyTorso, "0"),
        Number("ヘビー相手 頭 (ダウンまで)", r => r.HeavyHead, "0"),
        Number("店の最安値 (aUEC)", r => r.MinPrice, "#,0"),
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
        var ids = (IsShip ? GameDataQueryService.ShipWeaponTableCategories : GameDataQueryService.PersonalWeaponTableCategories)
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
