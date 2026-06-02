using System.Windows;
using System.Windows.Controls;

namespace StarCitizenJapaneseTextCreater;

public partial class InputAssignDialog : Window
{
    private readonly ActionMapData _data;
    private readonly string _inputCode;
    private readonly string _deviceType; // "mouse", "gamepad", "joystick1", "joystick2"
    private readonly List<ActionBinding> _allActions;
    private List<ActionBinding> _filteredActions = new();
    private bool _changed;

    public InputAssignDialog(ActionMapData data, string deviceType, string inputCode, string displayName, List<ActionBinding> currentBindings)
    {
        InitializeComponent();
        _data = data;
        _deviceType = deviceType;
        _inputCode = inputCode;
        _allActions = data.Categories.SelectMany(c => c.Actions).OrderBy(a => a.DisplayName).ToList();

        txtInputName.Text = $"{displayName}  ({inputCode})";
        lstCurrentBindings.ItemsSource = currentBindings;

        cmbActionCategory.Items.Add("全カテゴリ");
        foreach (var cat in data.Categories.OrderBy(c => c.Name))
            cmbActionCategory.Items.Add(cat.DisplayName);
        cmbActionCategory.SelectedIndex = 0;

        ApplyActionFilter();
    }

    private string GetBindingValue(ActionBinding a) => _deviceType switch
    {
        "mouse" => a.Mouse,
        "gamepad" => a.Gamepad,
        "joystick1" => a.Joystick1,
        "joystick2" => a.Joystick2,
        _ => ""
    };

    private string GetBindingDisplay(ActionBinding a) => _deviceType switch
    {
        "mouse" => a.MouseDisplay,
        "gamepad" => a.GamepadDisplay,
        "joystick1" => a.Joystick1Display,
        "joystick2" => a.Joystick2Display,
        _ => ""
    };

    private void SetBindingValue(ActionBinding a, string value)
    {
        switch (_deviceType)
        {
            case "mouse": a.Mouse = value; break;
            case "gamepad": a.Gamepad = value; break;
            case "joystick1": a.Joystick1 = value; break;
            case "joystick2": a.Joystick2 = value; break;
        }
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

        var wrapped = _filteredActions.Select(a => new ActionBindingWrapper(a, GetBindingDisplay(a))).ToList();
        dgAvailableActions.ItemsSource = wrapped;
    }

    private void ActionSearch_Changed(object sender, TextChangedEventArgs e) => ApplyActionFilter();
    private void ActionCategory_Changed(object sender, SelectionChangedEventArgs e) => ApplyActionFilter();

    private void AvailableAction_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (dgAvailableActions.SelectedItem is not ActionBindingWrapper wrapper) return;
        var action = wrapper.Binding;

        var existing = GetBindingDisplay(action);
        var msg = string.IsNullOrEmpty(existing)
            ? $"「{action.DisplayName}」に {InputDisplayHelper.FormatInput(_inputCode)} を割り当てますか？"
            : $"「{action.DisplayName}」の入力を\n{existing} → {InputDisplayHelper.FormatInput(_inputCode)}\nに変更しますか？";

        if (MessageBox.Show(msg, "確認", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        SetBindingValue(action, _inputCode);
        _changed = true;
        RefreshCurrentBindings();
        ApplyActionFilter();
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

        var deviceLabel = _deviceType switch
        {
            "mouse" => "マウス",
            "gamepad" => "ゲームパッド",
            "joystick1" => "HOTAS R",
            "joystick2" => "HOTAS L",
            _ => "入力"
        };

        if (MessageBox.Show($"「{binding.DisplayName}」の{deviceLabel}割り当てを解除しますか？",
            "確認", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        SetBindingValue(binding, "");
        _changed = true;
        RefreshCurrentBindings();
        ApplyActionFilter();
    }

    private void RefreshCurrentBindings()
    {
        var target = NormalizeInputCode(_inputCode);
        var allActions = _data.Categories.SelectMany(c => c.Actions).ToList();
        var bindings = allActions
            .Where(a => !string.IsNullOrEmpty(GetBindingValue(a)) &&
                        NormalizeInputCode(GetBindingValue(a)) == target)
            .ToList();
        lstCurrentBindings.ItemsSource = bindings;
    }

    private static string NormalizeInputCode(string input)
    {
        var parts = input.Split('+');
        var normalized = new List<string>();
        foreach (var part in parts)
            normalized.Add(part.Trim().ToLowerInvariant());
        normalized.Sort();
        return string.Join("+", normalized);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = _changed;
    }
}

public class ActionBindingWrapper
{
    public ActionBinding Binding { get; }
    public string CurrentInputDisplay { get; }
    public string DisplayName => Binding.DisplayName;
    public string CategoryDisplayName => Binding.CategoryDisplayName;

    public ActionBindingWrapper(ActionBinding binding, string currentInputDisplay)
    {
        Binding = binding;
        CurrentInputDisplay = currentInputDisplay;
    }
}
