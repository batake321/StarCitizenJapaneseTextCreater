using System.Windows;
using System.Windows.Controls;

namespace StarCitizenJapaneseTextCreater;

// ワイプ (サーバーリセット) で無くなる船を選んでまとめて削除するダイアログ。
// 既定では入手の種類 (my_ships.origin) がゲーム内購入 (ingame) ・ミッション (mission) の船だけを一覧に出し、全部チェック済み。
// 「すべての船を表示」で pledge・手入力の船も出せる (その場合の既定のチェックも ingame / mission だけ)。
// ワイプで消えるかはパッチによって違うので、決めつけはせず利用者が選ぶ
public class WipeShipRow
{
    public int Id { get; set; }
    public bool Selected { get; set; }
    public string Name { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Origin { get; set; } = "";          // my_ships.origin ('' = 手入力・不明 / pledge / ingame / mission)
    public string OriginDisplay { get; set; } = "";
    public string HangarCountDisplay { get; set; } = "";
    public bool IsPledge { get; set; }        // 同期済み保有船と名前が一致する (青字で見分ける)
    // ゲームログ由来 (ゲーム内購入・ミッション)。既定の一覧と既定のチェックはこれ
    public bool IsFromGameLog => Origin is MyShipEntry.OriginInGame or MyShipEntry.OriginMission;
}

public partial class ShipWipeDialog : Window
{
    private readonly List<WipeShipRow> _rows;
    // 今一覧に出している行 (「すべての船を表示」で切り替える)
    private List<WipeShipRow> _shown;

    // 削除対象に選ばれた所持船の id
    public List<int> SelectedShipIds { get; private set; } = new();

    public ShipWipeDialog(IEnumerable<WipeShipRow> rows)
    {
        InitializeComponent();
        _rows = rows.ToList();
        _shown = _rows;
        // ゲームログ由来の船が 1 隻も無ければ、最初からすべての船を出す (空の一覧にしない)
        if (!_rows.Any(r => r.IsFromGameLog)) chkShowAll.IsChecked = true;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        _shown = chkShowAll.IsChecked == true ? _rows : _rows.Where(r => r.IsFromGameLog).ToList();
        dgWipeShips.ItemsSource = null;
        dgWipeShips.ItemsSource = _shown;
        UpdateStatus();
    }

    private void ShowAll_Changed(object sender, RoutedEventArgs e)
    {
        if (dgWipeShips == null) return;   // XAML 読込中
        dgWipeShips.CommitEdit(DataGridEditingUnit.Row, true);
        ApplyFilter();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _shown) r.Selected = true;
        dgWipeShips.Items.Refresh();
        UpdateStatus();
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _shown) r.Selected = false;
        dgWipeShips.Items.Refresh();
        UpdateStatus();
    }

    // 「ゲーム内購入とミッションを選択」: 一覧の中のゲームログ由来の船だけにチェック
    private void SelectInGame_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _shown) r.Selected = r.IsFromGameLog;
        dgWipeShips.Items.Refresh();
        UpdateStatus();
    }

    // チェックボックスを直接触ったときも選択数を出し直す (編集確定後に数えるため Dispatcher 経由)
    private void Cell_EditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(UpdateStatus));
    }

    // 選択数は一覧に出ていない行 (すべての船を表示を外したときの pledge・手入力) も含めて数える (消すのは Selected の全部)
    private void UpdateStatus()
    {
        var hidden = _rows.Count - _shown.Count;
        txtWipeStatus.Text = $"{_rows.Count(r => r.Selected)} / {_rows.Count} 隻を選択" + (hidden > 0 ? $" (一覧に出ていない船 {hidden} 隻)" : "");
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        dgWipeShips.CommitEdit(DataGridEditingUnit.Row, true);

        if (!_rows.Any(r => r.Selected))
        {
            MessageBox.Show("削除する船が選ばれていません。", "ワイプ");
            return;
        }

        SelectedShipIds = _rows.Where(r => r.Selected).Select(r => r.Id).ToList();
        DialogResult = true;
    }
}
