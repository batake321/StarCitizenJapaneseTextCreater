using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace StarCitizenJapaneseTextCreater;

// 表の 1 列の定義。Text = 表に出す文字 (絞り込みの値にも使う)、Key = 並べ替えに使う値 (null は空欄。昇順・降順どちらでも最後)、
// Numeric = 数値の列 (右寄せ。絞り込みの値も数値の順に並べる)
public sealed class GridColumnSpec
{
    public string Header { get; init; } = "";
    public Func<object, string> Text { get; init; } = _ => "";
    public Func<object, IComparable?> Key { get; init; } = _ => null;
    public bool Numeric { get; init; }
}

// 絞り込み・複数列の並べ替えができる表 (［船舶戦闘］［個人戦闘］で共用)。
//   表の上の検索欄: どれかの列の文字に含まれる行だけを出す (大文字小文字を区別しない)
//   見出しの「▼」: その列の値の一覧 (検索欄・(すべて選択)・値ごとのチェックと件数) で、含む / 含まないを選ぶ。件数は全体の行数
//   見出しのクリック: その列で並べ替え (もう一度押すと逆順)。Shift を押しながらだと 2 つ目以降の並べ替えとして足す。
//   見出しに並べ替えの順位と ▲ (昇順) / ▼ (降順) を出す。空欄は昇順・降順どちらでも最後
public partial class FilterSortGrid : UserControl
{
    private sealed class GridRow
    {
        public object Source { get; init; } = new();
        public string[] Cells { get; init; } = [];
    }

    private List<GridColumnSpec> _columns = new();
    private List<GridRow> _rows = new();
    private readonly List<(int Column, bool Descending)> _sorts = new();
    private readonly Dictionary<int, HashSet<string>> _excluded = new();    // 列 → 含まない値
    private readonly List<(TextBlock Mark, Button Filter)> _headerParts = new();
    private const string BlankLabel = "(空欄)";

    // 表に出している行 (絞り込み・並べ替えの後) が変わったとき
    public event Action<IReadOnlyList<object>>? VisibleRowsChanged;

    public FilterSortGrid()
    {
        InitializeComponent();
    }

    // 列を決める (行・絞り込み・並べ替えは消える)
    public void SetColumns(IEnumerable<GridColumnSpec> columns)
    {
        _columns = columns.ToList();
        _sorts.Clear();
        _excluded.Clear();
        _headerParts.Clear();
        grid.Columns.Clear();
        for (int i = 0; i < _columns.Count; i++)
        {
            var spec = _columns[i];
            var index = i;
            var mark = new TextBlock { Margin = new Thickness(4, 0, 0, 0), Foreground = Brushes.DimGray };
            var filter = new Button
            {
                Content = "▼", Padding = new Thickness(3, 0, 3, 0), Margin = new Thickness(4, 0, 0, 0),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), FontSize = 10, Cursor = Cursors.Hand,
            };
            filter.Click += (_, _) => OpenFilter(index, filter);
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock { Text = spec.Header });
            header.Children.Add(mark);
            header.Children.Add(filter);
            _headerParts.Add((mark, filter));
            var column = new DataGridTextColumn { Header = header, Binding = new Binding($"Cells[{i}]"), SortMemberPath = $"Cells[{i}]" };
            if (spec.Numeric)
            {
                var style = new Style(typeof(TextBlock));
                style.Setters.Add(new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Right));
                column.ElementStyle = style;
            }
            grid.Columns.Add(column);
        }
        Refresh();
    }

    // 行を入れる。絞り込み (含まない値) と並べ替えはそのまま残す (新しく出てきた値は含む)
    public void SetItems(IEnumerable<object> items)
    {
        _rows = items.Select(o => new GridRow { Source = o, Cells = _columns.Select(c => c.Text(o) ?? "").ToArray() }).ToList();
        Refresh();
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

    // 見出しのクリック: DataGrid の並べ替えの代わりに、複数列の並べ替え (Shift で足す) を行う
    private void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var col = grid.Columns.IndexOf(e.Column);
        if (col < 0) return;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var pos = _sorts.FindIndex(s => s.Column == col);
        if (shift)
        {
            if (pos >= 0) _sorts[pos] = (col, !_sorts[pos].Descending);
            else _sorts.Add((col, false));
        }
        else
        {
            var desc = pos >= 0 && _sorts.Count == 1 && !_sorts[pos].Descending;
            _sorts.Clear();
            _sorts.Add((col, desc));
        }
        Refresh();
    }

    private void Refresh()
    {
        IEnumerable<GridRow> rows = _rows;
        var q = txtSearch?.Text?.Trim() ?? "";
        if (q.Length > 0)
            rows = rows.Where(r => r.Cells.Any(c => c.Contains(q, StringComparison.OrdinalIgnoreCase)));
        foreach (var (col, values) in _excluded)
            if (values.Count > 0) rows = rows.Where(r => !values.Contains(r.Cells[col]));
        var list = rows.ToList();
        // 安定な並べ替え (並べ替えの条件が同じ行は、入れたときの順を保つ)
        if (_sorts.Count > 0) list = list.OrderBy(r => r, Comparer<GridRow>.Create(CompareRows)).ToList();
        grid.ItemsSource = list;

        for (int i = 0; i < _headerParts.Count; i++)
        {
            var pos = _sorts.FindIndex(s => s.Column == i);
            _headerParts[i].Mark.Text = pos >= 0 ? (_sorts[pos].Descending ? "▼" : "▲") + (pos + 1) : "";
            var filtered = _excluded.TryGetValue(i, out var ex) && ex.Count > 0;
            _headerParts[i].Filter.Foreground = filtered ? Brushes.DodgerBlue : Brushes.Gray;
            _headerParts[i].Filter.FontWeight = filtered ? FontWeights.Bold : FontWeights.Normal;
        }
        if (txtCount != null) txtCount.Text = list.Count == _rows.Count ? $"{_rows.Count} 件" : $"{list.Count} / {_rows.Count} 件";
        VisibleRowsChanged?.Invoke(list.Select(r => r.Source).ToList());
    }

    // 並べ替え: 順位の順に比べる。空欄 (Key が null) はどの向きでも最後
    private int CompareRows(GridRow a, GridRow b)
    {
        foreach (var (col, desc) in _sorts)
        {
            var ka = _columns[col].Key(a.Source);
            var kb = _columns[col].Key(b.Source);
            if (ka == null && kb == null) continue;
            if (ka == null) return 1;
            if (kb == null) return -1;
            var c = ka is string sa && kb is string sb ? string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase) : ka.CompareTo(kb);
            if (c != 0) return desc ? -c : c;
        }
        return 0;
    }

    // 絞り込みの窓: 検索欄・(すべて選択)・値ごとのチェックと件数 (全体の行数)
    private void OpenFilter(int col, Button anchor)
    {
        var spec = _columns[col];
        if (!_excluded.TryGetValue(col, out var excluded)) _excluded[col] = excluded = new HashSet<string>(StringComparer.Ordinal);
        var values = _rows.GroupBy(r => r.Cells[col])
            .Select(g => (Value: g.Key, Count: g.Count(), Key: spec.Key(g.First().Source)))
            .OrderBy(v => v.Value.Length == 0 ? 1 : 0)
            .ThenBy(v => v.Key, Comparer<IComparable?>.Create((x, y) =>
                x == null && y == null ? 0 : x == null ? 1 : y == null ? -1
                : x is string sx && y is string sy ? string.Compare(sx, sy, StringComparison.OrdinalIgnoreCase) : x.CompareTo(y)))
            .ThenBy(v => v.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var search = new TextBox { Margin = new Thickness(0, 0, 0, 4), MinWidth = 160 };
        var all = new CheckBox { Content = "(すべて選択)", Margin = new Thickness(0, 0, 0, 2) };
        var listPanel = new StackPanel();
        var boxes = new List<(string Value, CheckBox Box)>();
        bool updating = false;

        void UpdateAll()
        {
            var shown = boxes.Where(b => b.Box.Visibility == Visibility.Visible).ToList();
            updating = true;
            all.IsChecked = shown.Count == 0 ? false
                : shown.All(b => b.Box.IsChecked == true) ? true
                : shown.All(b => b.Box.IsChecked != true) ? false : null;
            updating = false;
        }

        foreach (var (value, count, _) in values)
        {
            var box = new CheckBox { Content = $"{(value.Length == 0 ? BlankLabel : value)} ({count})", IsChecked = !excluded.Contains(value), Margin = new Thickness(0, 1, 0, 1) };
            var v = value;
            box.Checked += (_, _) => { if (updating) return; excluded.Remove(v); Refresh(); UpdateAll(); };
            box.Unchecked += (_, _) => { if (updating) return; excluded.Add(v); Refresh(); UpdateAll(); };
            boxes.Add((value, box));
            listPanel.Children.Add(box);
        }
        all.Click += (_, _) =>
        {
            var check = all.IsChecked == true;
            updating = true;
            foreach (var (value, box) in boxes.Where(b => b.Box.Visibility == Visibility.Visible))
            {
                box.IsChecked = check;
                if (check) excluded.Remove(value); else excluded.Add(value);
            }
            updating = false;
            Refresh();
            UpdateAll();
        };
        search.TextChanged += (_, _) =>
        {
            var t = search.Text.Trim();
            foreach (var (value, box) in boxes)
                box.Visibility = t.Length == 0 || (value.Length == 0 ? BlankLabel : value).Contains(t, StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible : Visibility.Collapsed;
            UpdateAll();
        };
        UpdateAll();

        var panel = new StackPanel { Margin = new Thickness(8) };
        panel.Children.Add(search);
        panel.Children.Add(all);
        panel.Children.Add(new ScrollViewer { Content = listPanel, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var popup = new Popup
        {
            PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = false,
            Child = new Border { Background = Brushes.White, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = panel },
        };
        popup.Opened += (_, _) => search.Focus();
        popup.IsOpen = true;
    }
}
