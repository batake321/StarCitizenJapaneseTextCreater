using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace StarCitizenJapaneseTextCreater;

// 最上位のタブ (MainWindow の tabMain) をドラッグで並べ替える。
// ヘッダーを押して閾値 (SystemParameters.MinimumHorizontal/VerticalDragDistance) を超えて動かしたら DoDragDrop を始め、
// 落とした先のヘッダーの左右どちらの半分かで、その前か後ろに挿す。対象は tabMain 直下の TabItem だけ
// (入れ子の TabControl の TabItem は ItemsControlFromItemContainer が tabMain でないので対象外)。
// 並びは TabItem の x:Name の一覧 (AppConfig.TabOrder) で保存・復元する
internal static class TabReorder
{
    private const string DataFormat = "StarCitizenJapaneseTextCreater.TabItem";

    // ドラッグの受け付けを tab に付ける。並びが変わったら onReordered を呼ぶ (保存に使う)
    public static void Attach(TabControl tab, Action? onReordered)
    {
        tab.AllowDrop = true;
        TabItem? pressed = null;
        var start = new Point();

        tab.PreviewMouseLeftButtonDown += (_, e) =>
        {
            pressed = HeaderItemAt(tab, e.OriginalSource);
            start = e.GetPosition(tab);
        };
        tab.PreviewMouseLeftButtonUp += (_, _) => pressed = null;
        tab.PreviewMouseMove += (_, e) =>
        {
            if (pressed == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { pressed = null; return; }
            var p = e.GetPosition(tab);
            if (Math.Abs(p.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(p.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var item = pressed;
            pressed = null;
            DragDrop.DoDragDrop(item, new DataObject(DataFormat, item), DragDropEffects.Move);
        };
        tab.DragOver += (_, e) =>
        {
            if (!e.Data.GetDataPresent(DataFormat)) return;   // ほかのドラッグ (ファイル等) には触らない
            e.Effects = HeaderItemAt(tab, e.OriginalSource) != null ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        tab.Drop += (_, e) =>
        {
            if (!e.Data.GetDataPresent(DataFormat) || e.Data.GetData(DataFormat) is not TabItem dragged) return;
            var target = HeaderItemAt(tab, e.OriginalSource);
            if (target == null) return;
            e.Handled = true;
            var after = e.GetPosition(target).X >= target.ActualWidth / 2;
            if (Move(tab, dragged, target, after)) onReordered?.Invoke();
        };
    }

    // dragged を target の前 (after = false) か後ろ (after = true) へ動かす。選択中のタブは Remove の前に覚えて戻す。
    // 並びが変わったら true
    public static bool Move(TabControl tab, TabItem dragged, TabItem target, bool after)
    {
        if (ReferenceEquals(dragged, target)) return false;
        var items = tab.Items;
        var from = items.IndexOf(dragged);
        if (from < 0 || items.IndexOf(target) < 0) return false;
        var selected = tab.SelectedItem;
        items.Remove(dragged);
        var idx = items.IndexOf(target) + (after ? 1 : 0);
        items.Insert(idx, dragged);
        tab.SelectedItem = selected;
        return items.IndexOf(dragged) != from;
    }

    // 今の並び (x:Name の一覧。名前の無い TabItem は入れない)
    public static List<string> GetOrder(TabControl tab) =>
        tab.Items.OfType<TabItem>().Select(t => t.Name).Where(n => !string.IsNullOrEmpty(n)).ToList();

    // 保存した並び (x:Name の一覧) に並べ直す。一覧に無いタブは今の順のまま末尾、存在しない名は無視。選択中のタブは保つ
    public static void ApplyOrder(TabControl tab, IEnumerable<string>? order)
    {
        if (order == null) return;
        var items = tab.Items;
        var byName = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var t in items.OfType<TabItem>())
            if (!string.IsNullOrEmpty(t.Name) && !byName.ContainsKey(t.Name)) byName[t.Name] = t;
        var desired = new List<object>();
        foreach (var name in order)
            if (name != null && byName.TryGetValue(name, out var t) && !desired.Contains(t)) desired.Add(t);
        foreach (var item in items)
            if (!desired.Contains(item)) desired.Add(item);
        if (desired.SequenceEqual(items.Cast<object>())) return;

        var selected = tab.SelectedItem;
        for (int i = 0; i < desired.Count; i++)
        {
            if (ReferenceEquals(items[i], desired[i])) continue;
            items.Remove(desired[i]);
            items.Insert(i, desired[i]);
        }
        tab.SelectedItem = selected;
    }

    // x:Name のタブ。無ければ null
    public static TabItem? Find(TabControl tab, string? name) =>
        string.IsNullOrEmpty(name) ? null : tab.Items.OfType<TabItem>().FirstOrDefault(t => t.Name == name);

    // マウスの下 (イベントの OriginalSource) にある、tab 直下の TabItem (ヘッダー)。無ければ null。
    // 選択中のタブの中身は TabControl の ContentPresenter に出ていて TabItem の下には無いので、中身を押してもここには来ない。
    // 入れ子の TabControl の TabItem に当たったら、その親は tab ではないので null (そこで止める)
    private static TabItem? HeaderItemAt(TabControl tab, object? source)
    {
        var d = source as DependencyObject;
        // Run など Visual でないものは、Visual になるまで論理ツリーの親をたどる
        while (d != null && d is not Visual && d is not System.Windows.Media.Media3D.Visual3D)
            d = LogicalTreeHelper.GetParent(d);
        while (d != null)
        {
            if (d is TabItem ti)
                return ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(ti), tab) ? ti : null;
            if (ReferenceEquals(d, tab)) return null;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }
}
