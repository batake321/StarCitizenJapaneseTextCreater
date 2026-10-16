using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace StarCitizenJapaneseTextCreater;

// 表の 1 列の定義。Text = 表に出す文字 (絞り込みの値にも使う)、Key = 並べ替えに使う値 (null は空欄。昇順・降順どちらでも最後)、
// Numeric = 数値の列 (右寄せ。絞り込みの値も数値の順に並べる)、Filterable = 見出しに絞り込みのボタン (Select) を出すか (並べ替えはどの列でもできる)
public sealed class GridColumnSpec
{
    public bool Filterable { get; init; } = true;
    public string Header { get; init; } = "";
    public Func<object, string> Text { get; init; } = _ => "";
    public Func<object, IComparable?> Key { get; init; } = _ => null;
    public bool Numeric { get; init; }
    // セルのツールチップ (null・空なら出さない)。指定が無い列は出さない
    public Func<object, string?>? Tooltip { get; init; }
    // ボタンの列にするとき: 行ごとに null = ボタンを出さない、true = 押せる、false = 押せない (文字は Text)。押されたら ButtonClick (引数は行の元のオブジェクト)
    public Func<object, bool?>? Button { get; init; }
    public Action<object>? ButtonClick { get; init; }
}

// ボタンの列のセル 1 つ (GridColumnSpec.Button)
public sealed class GridCellButton
{
    public string Text { get; init; } = "";
    public bool IsEnabled { get; init; }
    public Visibility Visibility { get; init; }
}

// 絞り込み・複数列の並べ替えができる表 (［船舶戦闘］［個人戦闘］で共用)。
//   表の上の検索欄: どれかの列の文字に含まれる行だけを出す (大文字小文字を区別しない)
//   見出しの「Select」: その列の値の一覧 (検索欄・全て・値ごとのチェックと件数) で、含む / 含まないを選ぶ。件数は全体の行数
//   見出しのクリック: その列で並べ替え (もう一度押すと逆順)。Shift を押しながらだと 2 つ目以降の並べ替えとして足す。
//   見出しに並べ替えの順位と ▲ (昇順) / ▼ (降順) を出す。空欄は昇順・降順どちらでも最後
public partial class FilterSortGrid : UserControl
{
    // 名前の列 (1 列目) のセルをダブルクリックしたとき。引数は行の元のオブジェクト (SetItems に渡したもの)
    public event Action<object>? NameDoubleClicked;

    private sealed class GridRow
    {
        public object Source { get; init; } = new();
        public string[] Cells { get; init; } = [];
        public string?[] Tips { get; init; } = [];          // セルのツールチップ (GridColumnSpec.Tooltip。無ければ null)
        public GridCellButton?[] Buttons { get; init; } = [];  // ボタンの列のセル (GridColumnSpec.Button。ボタンの列でなければ null)
    }

    private List<GridColumnSpec> _columns = new();
    private List<GridRow> _rows = new();
    private readonly List<(int Column, bool Descending)> _sorts = new();
    private readonly Dictionary<int, HashSet<string>> _excluded = new();    // 列 → 含まない値
    private readonly List<(TextBlock Title, TextBlock Mark, Button Filter)> _headerParts = new();
    private const string BlankLabel = "(空欄)";

    // 表に出している行 (絞り込み・並べ替えの後) が変わったとき
    public event Action<IReadOnlyList<object>>? VisibleRowsChanged;

    public FilterSortGrid()
    {
        InitializeComponent();
        // Ctrl+C で選んだセルの文字だけをコピーする (DataGridCellCopy)
        DataGridCellCopy.Attach(grid);
        // 名前の列 (1 列目) のダブルクリック → NameDoubleClicked
        grid.MouseDoubleClick += (_, e) =>
        {
            var cell = FindParent<DataGridCell>(e.OriginalSource as DependencyObject);
            if (cell == null || grid.Columns.IndexOf(cell.Column) != 0 || cell.DataContext is not GridRow row) return;
            NameDoubleClicked?.Invoke(row.Source);
        };
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
                Content = "Select", Padding = new Thickness(2, 0, 2, 0), Margin = new Thickness(4, 0, 0, 0),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), FontSize = 10, Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = spec.Filterable ? Visibility.Visible : Visibility.Collapsed,
            };
            filter.Click += (_, _) => OpenFilter(index, filter);
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            var title = new TextBlock { Text = spec.Header };
            header.Children.Add(title);
            header.Children.Add(mark);
            header.Children.Add(filter);
            _headerParts.Add((title, mark, filter));
            if (spec.Button != null)
            {
                // ボタンの列: 行の Buttons[i] の文字・押せるか・出すかで作る
                var factory = new FrameworkElementFactory(typeof(Button));
                factory.SetBinding(ContentControl.ContentProperty, new Binding($"Buttons[{i}].Text"));
                factory.SetBinding(UIElement.IsEnabledProperty, new Binding($"Buttons[{i}].IsEnabled"));
                factory.SetBinding(UIElement.VisibilityProperty, new Binding($"Buttons[{i}].Visibility"));
                factory.SetValue(Control.PaddingProperty, new Thickness(6, 0, 6, 0));
                factory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
                factory.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler((s, _) =>
                {
                    if ((s as FrameworkElement)?.DataContext is GridRow row) spec.ButtonClick?.Invoke(row.Source);
                }));
                grid.Columns.Add(new DataGridTemplateColumn
                {
                    Header = header, SortMemberPath = $"Cells[{i}]",
                    CellTemplate = new DataTemplate { VisualTree = factory },
                });
                continue;
            }
            var column = new DataGridTextColumn { Header = header, Binding = new Binding($"Cells[{i}]"), SortMemberPath = $"Cells[{i}]" };
            if (spec.Numeric || spec.Tooltip != null)
            {
                var style = new Style(typeof(TextBlock));
                if (spec.Numeric) style.Setters.Add(new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Right));
                // ツールチップは null のセルでは出ない
                if (spec.Tooltip != null) style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding($"Tips[{i}]")));
                column.ElementStyle = style;
            }
            grid.Columns.Add(column);
        }
        Refresh();
    }

    // 行を入れる。絞り込み (含まない値) と並べ替えはそのまま残す (新しく出てきた値は含む)
    public void SetItems(IEnumerable<object> items)
    {
        _rows = items.Select(o => new GridRow
        {
            Source = o,
            Cells = _columns.Select(c => c.Text(o) ?? "").ToArray(),
            Tips = _columns.Select(c => c.Tooltip?.Invoke(o) is { Length: > 0 } t ? t : null).ToArray(),
            Buttons = _columns.Select(c => MakeButton(c, o)).ToArray(),
        }).ToList();
        Refresh();
    }

    private static GridCellButton? MakeButton(GridColumnSpec spec, object o)
    {
        if (spec.Button == null) return null;
        var state = spec.Button(o);
        return new GridCellButton
        {
            Text = spec.Text(o) ?? "",
            IsEnabled = state == true,
            Visibility = state == null ? Visibility.Collapsed : Visibility.Visible,
        };
    }

    // 1 列の定義 (見出し・値) を差し替える。その列は値が変わるので絞り込みを解除する。並べ替えと他の列の絞り込み・検索欄はそのまま残す
    public void ReplaceColumn(int index, GridColumnSpec spec)
    {
        if (index < 0 || index >= _columns.Count) return;
        _columns[index] = spec;
        _headerParts[index].Title.Text = spec.Header;
        _headerParts[index].Filter.Visibility = spec.Filterable ? Visibility.Visible : Visibility.Collapsed;
        _excluded.Remove(index);
        foreach (var r in _rows)
        {
            r.Cells[index] = spec.Text(r.Source) ?? "";
            r.Tips[index] = spec.Tooltip?.Invoke(r.Source) is { Length: > 0 } t ? t : null;
            r.Buttons[index] = MakeButton(spec, r.Source);
        }
        Refresh();
    }

    // 表の上の検索欄の並び (右側) に、使う側の小さな部品 (選択など) を足す
    public void AddToolbarItem(UIElement element) => toolbarExtra.Children.Add(element);

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
            _headerParts[i].Filter.Foreground = filtered ? Brushes.Gold : Brushes.DodgerBlue;
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

    // 絞り込みの窓: 検索欄・全て・値ごとのチェックと件数 (全体の行数)
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
        var all = new CheckBox { Content = "全て", Margin = new Thickness(0, 0, 0, 2) };
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

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }
}
