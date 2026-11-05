using System.Windows;
using System.Windows.Controls;

namespace StarCitizenJapaneseTextCreater;

// 翻訳タブの [日本語化] を押したときに、印を選ぶダイアログ (InGameMarksDialog) の前に出す、日本語化の適応範囲を選ぶダイアログ。
// 「全て翻訳」か「一部翻訳」を選び、一部翻訳ならチェックを付けた種類 (TranslationScope.Categories) だけを日本語にする。
// 「全て翻訳」のときはチェックボックスを押せない形にする
public partial class TranslationScopeDialog : Window
{
    private readonly List<(string Id, CheckBox Box)> _boxes = new();

    public bool PartialTranslation => rbPartial.IsChecked == true;
    // チェックを外した種類の Id (TranslationScope.Categories の並び)
    public List<string> UncheckedCategories => _boxes.Where(b => b.Box.IsChecked != true).Select(b => b.Id).ToList();

    public TranslationScopeDialog(bool partialTranslation, IEnumerable<string> uncheckedCategories)
    {
        InitializeComponent();
        var unchecked_ = new HashSet<string>(uncheckedCategories, StringComparer.Ordinal);
        foreach (var c in TranslationScope.Categories)
        {
            var box = new CheckBox { Content = c.Label, Margin = new Thickness(0, 4, 0, 4), IsChecked = !unchecked_.Contains(c.Id) };
            panelCategories.Children.Add(box);
            _boxes.Add((c.Id, box));
        }
        if (partialTranslation) rbPartial.IsChecked = true;
        else rbAll.IsChecked = true;
        UpdateEnabled();
    }

    private void Scope_Changed(object sender, RoutedEventArgs e) => UpdateEnabled();

    // 「全て翻訳」のときはチェックボックスを押せなくする (InitializeComponent の途中で呼ばれたときは panelCategories がまだ無い)
    private void UpdateEnabled()
    {
        if (panelCategories != null) panelCategories.IsEnabled = rbPartial.IsChecked == true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
