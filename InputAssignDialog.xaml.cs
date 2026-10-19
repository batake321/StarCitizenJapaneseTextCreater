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
        _inputCode = InputCodeHelper.ToGameForm(inputCode, null);   // ゲームの形 ("kb1_rctrl+js1_button3" → "js1_rctrl+button3")
        _allActions = data.Categories.SelectMany(c => c.Actions).OrderBy(a => a.DisplayName).ToList();

        txtInputName.Text = $"{displayName}  ({_inputCode})";
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
        "mouse" => a.MouseDisplayJa,
        "gamepad" => a.GamepadDisplayJa,
        "joystick1" => a.Joystick1DisplayJa,
        "joystick2" => a.Joystick2DisplayJa,
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
            ? $"「{action.DisplayName}」に {InputDisplayHelper.FormatInputJa(_inputCode)} を割り当てますか？"
            : $"「{action.DisplayName}」の入力を\n{existing} → {InputDisplayHelper.FormatInputJa(_inputCode)}\nに変更しますか？";

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
            "joystick1" => $"HOTAS {_data.GetJsSideLabel(1)}",
            "joystick2" => $"HOTAS {_data.GetJsSideLabel(2)}",
            _ => "入力"
        };

        if (MessageBox.Show($"「{binding.DisplayName}」の{deviceLabel}割り当てを解除しますか？",
            "確認", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        // 割り当て解除はゲームの解除の書き方 (機器の接頭辞 + 空白。"js1_ " など) で持つ (押した直後から「（解除）」と出る)
        var clearValue = _deviceType switch
        {
            "mouse" => "mo1_ ",
            "gamepad" => "gp1_ ",
            "joystick1" => "js1_ ",
            "joystick2" => "js2_ ",
            _ => "",
        };
        SetBindingValue(binding, clearValue);
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
        // 接頭辞は先頭の部分にしか付かない (ゲームの形) ので、並べ替えの前に各部分の接頭辞を外す
        foreach (var part in parts)
            normalized.Add(InputCodeHelper.StripDevicePrefix(part).ToLowerInvariant());
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
