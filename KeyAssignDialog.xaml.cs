using System.Windows;
using System.Windows.Controls;

namespace StarCitizenJapaneseTextCreater;

public partial class KeyAssignDialog : Window
{
    private readonly ActionMapData _data;
    private readonly string _keyCode;
    private readonly List<ActionBinding> _allActions;
    private List<ActionBinding> _filteredActions = new();
    private bool _changed;

    public KeyAssignDialog(ActionMapData data, string keyCode, string displayKey, List<ActionBinding> currentBindings)
    {
        InitializeComponent();
        _data = data;
        _keyCode = keyCode;
        _allActions = data.Categories.SelectMany(c => c.Actions).OrderBy(a => a.DisplayName).ToList();

        txtKeyName.Text = $"キー: {displayKey}  ({keyCode})";
        lstCurrentBindings.ItemsSource = currentBindings;

        cmbActionCategory.Items.Add("全カテゴリ");
        foreach (var cat in data.Categories.OrderBy(c => c.Name))
            cmbActionCategory.Items.Add(cat.DisplayName);
        cmbActionCategory.SelectedIndex = 0;

        ApplyActionFilter();
    }

    private void ApplyActionFilter()
    {
        var search = txtActionSearch?.Text?.Trim() ?? "";
        var catDisplay = cmbActionCategory?.SelectedItem?.ToString() ?? "全カテゴリ";

        _filteredActions = _allActions;

        if (catDisplay != "全カテゴリ")
            _filteredActions = _filteredActions
                .Where(a => ActionMapNames.GetCategoryName(a.CategoryName) == catDisplay).ToList();

        if (!string.IsNullOrEmpty(search))
            _filteredActions = _filteredActions
                .Where(a => a.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            a.ActionName.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();

        dgAvailableActions.ItemsSource = _filteredActions;
    }

    private void ActionSearch_Changed(object sender, TextChangedEventArgs e) => ApplyActionFilter();
    private void ActionCategory_Changed(object sender, SelectionChangedEventArgs e) => ApplyActionFilter();

    private void AvailableAction_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (dgAvailableActions.SelectedItem is not ActionBinding action) return;

        var existing = action.KeyboardDisplay;
        var msg = string.IsNullOrEmpty(existing)
            ? $"「{action.DisplayName}」に {_keyCode} を割り当てますか？"
            : $"「{action.DisplayName}」のキーを\n{existing} → {InputDisplayHelper.FormatInput(_keyCode)}\nに変更しますか？";

        if (MessageBox.Show(msg, "確認", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        action.Keyboard = _keyCode;
        _changed = true;
        RefreshCurrentBindings();
        dgAvailableActions.Items.Refresh();
    }

    private void EditBinding_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ActionBinding binding) return;
        var dlg = new BindingEditDialog(binding) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _changed = true;
            RefreshCurrentBindings();
        }
    }

    private void RemoveBinding_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ActionBinding binding) return;

        if (MessageBox.Show($"「{binding.DisplayName}」のキーボード割り当てを解除しますか？",
            "確認", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        binding.Keyboard = "";
        _changed = true;
        RefreshCurrentBindings();
        dgAvailableActions.Items.Refresh();
    }

    private static string NormalizeKeyCode(string input)
    {
        var parts = input.Split('+');
        var normalized = new List<string>();
        foreach (var part in parts)
        {
            var p = part.Trim();
            if (p.StartsWith("kb1_", StringComparison.OrdinalIgnoreCase))
                p = p[4..];
            normalized.Add(p.ToLowerInvariant());
        }
        normalized.Sort();
        return string.Join("+", normalized);
    }

    private void RefreshCurrentBindings()
    {
        var target = NormalizeKeyCode(_keyCode);
        var allActions = _data.Categories.SelectMany(c => c.Actions).ToList();
        var bindings = allActions
            .Where(a => !string.IsNullOrEmpty(a.Keyboard) &&
                        NormalizeKeyCode(a.Keyboard) == target)
            .ToList();
        lstCurrentBindings.ItemsSource = bindings;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = _changed;
    }
}
