using System.Windows;
using System.Windows.Controls;

namespace StarCitizenJapaneseTextCreater;

// ワイプ (サーバーリセット) で無くなる船を選んでまとめて削除するダイアログ。
// 既定のチェックは「ゲーム内購入 (aUEC)」の船だけ。pledge で持っている船はワイプで消えないので既定は外す
public class WipeShipRow
{
    public int Id { get; set; }
    public bool Selected { get; set; }
    public string Name { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string OriginDisplay { get; set; } = "";
    public string HangarCountDisplay { get; set; } = "";
    public bool IsPledge { get; set; }        // 同期済み保有船と名前が一致する (= ワイプで消えない)
}

public partial class ShipWipeDialog : Window
{
    private readonly List<WipeShipRow> _rows;

    // 削除対象に選ばれた所持船の id
    public List<int> SelectedShipIds { get; private set; } = new();

    public ShipWipeDialog(IEnumerable<WipeShipRow> rows)
    {
        InitializeComponent();
        _rows = rows.ToList();
        dgWipeShips.ItemsSource = _rows;
        UpdateStatus();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.Selected = true;
        dgWipeShips.Items.Refresh();
        UpdateStatus();
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.Selected = false;
        dgWipeShips.Items.Refresh();
        UpdateStatus();
    }

    private void SelectInGame_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.Selected = !r.IsPledge;
        dgWipeShips.Items.Refresh();
        UpdateStatus();
    }

    // チェックボックスを直接触ったときも選択数を出し直す (編集確定後に数えるため Dispatcher 経由)
    private void Cell_EditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(UpdateStatus));
    }

    private void UpdateStatus()
    {
        txtWipeStatus.Text = $"{_rows.Count(r => r.Selected)} / {_rows.Count} 隻を選択";
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
