using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace StarCitizenJapaneseTextCreater;

// 表 (DataGrid) で Ctrl+C を押したとき、行全体ではなく今のセル (クリックしたセル) の文字だけをクリップボードに入れる。
// 行の選択 (SelectionMode / SelectionUnit) は変えない (SelectedItem を使う処理をそのまま動かすため)。
// 文字は列のクリップボード用の値 (DataGridColumn.OnCopyingCellClipboardContent。文字の列は表示と同じ Binding)、
// それが無い列 (テンプレートの列) はセルの中の TextBlock の文字をつないだもの。
// 編集中の入力欄 (TextBox) での Ctrl+C は入力欄のコピーのまま (何もしない)
public static class DataGridCellCopy
{
    public static void Attach(DataGrid grid)
    {
        grid.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.C || Keyboard.Modifiers != ModifierKeys.Control) return;
            if (e.OriginalSource is TextBox) return;
            var text = CurrentCellText(grid);
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); } catch { }   // クリップボードを他のアプリが開いているときは諦める
            e.Handled = true;
        };
    }

    private static string? CurrentCellText(DataGrid grid)
    {
        var cell = grid.CurrentCell;
        if (!cell.IsValid || cell.Column == null || cell.Item == null) return null;
        string? text = null;
        try { text = cell.Column.OnCopyingCellClipboardContent(cell.Item)?.ToString(); } catch { }
        if (string.IsNullOrEmpty(text) && cell.Column.GetCellContent(cell.Item) is { } content) text = VisualText(content);
        return text?.Trim();
    }

    // 部品の中の TextBlock の文字を「 」でつなぐ (ComboBox は選んでいる項目の文字)
    private static string VisualText(DependencyObject root)
    {
        if (root is TextBlock tb) return tb.Text;
        if (root is ComboBox cb) return cb.Text;
        var parts = new List<string>();
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var t = VisualText(VisualTreeHelper.GetChild(root, i));
            if (t.Length > 0) parts.Add(t);
        }
        return string.Join(" ", parts);
    }
}
