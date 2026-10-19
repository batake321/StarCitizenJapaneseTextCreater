using System.Windows;
using System.Windows.Controls;

namespace StarCitizenJapaneseTextCreater;

public partial class BindingEditDialog : Window
{
    private readonly ActionBinding _original;
    private readonly ActionMapData _data;   // js 番号の対応と、他の機能の一覧に使う (binding.Owner)
    private string _editTarget = "keyboard";

    // 物理コントローラー / HOTAS の押下の待ち受け (ダイアログを開いている間)
    private GameControllerInput? _controllerInput;
    // 確認欄に出している、反映待ちの入力 (null = 反映できない / 確認欄なし)
    private (string DeviceType, string Value)? _pendingPress;

    // Editing copies - not applied until OK
    private string _keyboard;
    private string _mouse;
    private string _gamepad;
    private string _joystick1;
    private string _joystick2;
    private bool _isLongPress;

    private static readonly List<InputItem> KeyboardKeys = new()
    {
        new("A","a"), new("B","b"), new("C","c"), new("D","d"), new("E","e"), new("F","f"),
        new("G","g"), new("H","h"), new("I","i"), new("J","j"), new("K","k"), new("L","l"),
        new("M","m"), new("N","n"), new("O","o"), new("P","p"), new("Q","q"), new("R","r"),
        new("S","s"), new("T","t"), new("U","u"), new("V","v"), new("W","w"), new("X","x"),
        new("Y","y"), new("Z","z"),
        new("1","1"), new("2","2"), new("3","3"), new("4","4"), new("5","5"),
        new("6","6"), new("7","7"), new("8","8"), new("9","9"), new("0","0"),
        new("F1","f1"), new("F2","f2"), new("F3","f3"), new("F4","f4"),
        new("F5","f5"), new("F6","f6"), new("F7","f7"), new("F8","f8"),
        new("F9","f9"), new("F10","f10"), new("F11","f11"), new("F12","f12"),
        new("Space","space"), new("Tab","tab"), new("Enter","enter"),
        new("Esc","escape"), new("Backspace","backspace"),
        new("Up","up"), new("Down","down"), new("Left","left"), new("Right","right"),
        new("Insert","insert"), new("Delete","delete"),
        new("Home","home"), new("End","end"), new("PgUp","pgup"), new("PgDn","pgdn"),
        new("Num0","np_0"), new("Num1","np_1"), new("Num2","np_2"), new("Num3","np_3"),
        new("Num4","np_4"), new("Num5","np_5"), new("Num6","np_6"), new("Num7","np_7"),
        new("Num8","np_8"), new("Num9","np_9"),
        new("Num+","np_add"), new("Num-","np_subtract"), new("Num*","np_multiply"), new("Num/","np_divide"),
        new("NumEnter","np_enter"),
        new("-","minus"), new("=","equals"), new("[","lbracket"), new("]","rbracket"),
        new(";","semicolon"), new("'","apostrophe"), new(",","comma"), new(".","period"),
        new("/","slash"), new("\\","backslash"), new("`","grave"),
    };

    private static readonly List<InputItem> MouseKeys = new()
    {
        new("Left Click","mo1_mouse1"), new("Right Click","mo1_mouse2"), new("Middle Click","mo1_mouse3"),
        new("Mouse4","mo1_mouse4"), new("Mouse5","mo1_mouse5"),
        new("Wheel Up","mo1_mwheel_up"), new("Wheel Down","mo1_mwheel_down"),
        new("Mouse X","mo1_maxis_x"), new("Mouse Y","mo1_maxis_y"),
    };

    private static readonly List<InputItem> GamepadKeys = new()
    {
        new("A","gp1_a"), new("B","gp1_b"), new("X","gp1_x"), new("Y","gp1_y"),
        new("LB","gp1_shoulderl"), new("RB","gp1_shoulderr"),
        new("LT","gp1_triggerl_btn"), new("RT","gp1_triggerr_btn"),
        new("L Stick Press","gp1_thumbl"), new("R Stick Press","gp1_thumbr"),
        new("L Stick X","gp1_thumblx"), new("L Stick Y","gp1_thumbly"),
        new("R Stick X","gp1_thumbrx"), new("R Stick Y","gp1_thumbry"),
        new("D-Pad Up","gp1_dpad_up"), new("D-Pad Down","gp1_dpad_down"),
        new("D-Pad Left","gp1_dpad_left"), new("D-Pad Right","gp1_dpad_right"),
        new("Start","gp1_start"), new("Back","gp1_back"),
    };

    // 一覧から選べるジョイスティックのボタン番号の上限
    private const int MaxJoystickButton = 128;

    // Logical joystick inputs (no js1_/js2_ prefix). Prefix is applied based on edit target.
    //   ボタンは 1〜MaxJoystickButton (128)、続けて軸・ハット (ハットは 1〜2)
    private static readonly List<InputItem> JoystickKeysBase = Enumerable.Range(1, MaxJoystickButton)
        .Select(n => new InputItem($"Btn {n}", $"button{n}"))
        .Concat(new List<InputItem>
    {
        new("X Axis","x"), new("Y Axis","y"), new("Z Axis","z"),
        new("RX","rotx"), new("RY","roty"), new("RZ","rotz"),
        new("Slider 1","slider1"), new("Slider 2","slider2"),
        new("Hat1 Up","hat1_up"), new("Hat1 Down","hat1_down"),
        new("Hat1 Left","hat1_left"), new("Hat1 Right","hat1_right"),
        new("Hat2 Up","hat2_up"), new("Hat2 Down","hat2_down"),
        new("Hat2 Left","hat2_left"), new("Hat2 Right","hat2_right"),
    }).ToList();

    public BindingEditDialog(ActionBinding binding)
    {
        InitializeComponent();
        _original = binding;
        _data = binding.Owner ?? new ActionMapData();

        _keyboard = binding.Keyboard;
        _mouse = binding.Mouse;
        _gamepad = binding.Gamepad;
        _joystick1 = binding.Joystick1;
        _joystick2 = binding.Joystick2;
        _isLongPress = binding.IsLongPress;

        txtActionName.Text = $"{binding.DisplayName} ({binding.ActionName})";
        txtCategory.Text = ActionMapNames.GetCategoryName(binding.CategoryName);

        lblJoystick1.Text = $"HOTAS {_data.GetJsSideLabel(1)}:";
        lblJoystick2.Text = $"HOTAS {_data.GetJsSideLabel(2)}:";

        UpdateFields();
        ShowKeyList("keyboard");

        try
        {
            _controllerInput = new GameControllerInput();
            _controllerInput.InputPressed += ControllerInput_Pressed;
            _controllerInput.Start();
        }
        catch (Exception)
        {
            // 待ち受けを始められない環境では押下での入力は使えない (リストからの選択は従来どおり)
            _controllerInput?.Dispose();
            _controllerInput = null;
        }
        Closed += (_, _) =>
        {
            if (_controllerInput != null)
            {
                _controllerInput.InputPressed -= ControllerInput_Pressed;
                _controllerInput.Dispose();
                _controllerInput = null;
            }
        };
    }

    private void UpdateFields()
    {
        txtKeyboard.Text = InputDisplayHelper.FormatInputJa(_keyboard);
        txtMouse.Text = InputDisplayHelper.FormatInputJa(_mouse);
        txtGamepad.Text = InputDisplayHelper.FormatInputJa(_gamepad);
        txtJoystickR.Text = InputDisplayHelper.FormatInputJa(_joystick1);
        txtJoystickL.Text = InputDisplayHelper.FormatInputJa(_joystick2);
        chkLongPress.IsChecked = _isLongPress;
    }

    private void ShowKeyList(string target)
    {
        _editTarget = target;
        UpdateModifierAvailability(target);
        grpSelector.Header = target switch
        {
            "keyboard" => "キーボード入力を選択",
            "mouse" => "マウス入力を選択",
            "gamepad" => "ゲームパッド入力を選択",
            "joystick1" => $"HOTAS {_data.GetJsSideLabel(1)} 入力を選択 ({_data.GetJsHeading(1)})",
            "joystick2" => $"HOTAS {_data.GetJsSideLabel(2)} 入力を選択 ({_data.GetJsHeading(2)})",
            _ => "入力を選択"
        };

        lstKeys.ItemsSource = target switch
        {
            "keyboard" => KeyboardKeys,
            "mouse" => MouseKeys,
            "gamepad" => GamepadKeys,
            "joystick1" => JoystickKeysBase,
            "joystick2" => JoystickKeysBase,
            _ => KeyboardKeys
        };
    }

    private void SetKeyboard_Click(object sender, RoutedEventArgs e) => ShowKeyList("keyboard");
    private void SetMouse_Click(object sender, RoutedEventArgs e) => ShowKeyList("mouse");
    private void SetGamepad_Click(object sender, RoutedEventArgs e) => ShowKeyList("gamepad");
    private void SetJoystickR_Click(object sender, RoutedEventArgs e) => ShowKeyList("joystick1");
    private void SetJoystickL_Click(object sender, RoutedEventArgs e) => ShowKeyList("joystick2");

    private void Key_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (lstKeys.SelectedItem is not InputItem item) return;

        var inputValue = _editTarget switch
        {
            "keyboard" => $"kb1_{item.Value}",
            "joystick1" => $"js1_{item.Value}",
            "joystick2" => $"js2_{item.Value}",
            _ => item.Value,
        };

        inputValue = AddCheckedModifiers(inputValue, _editTarget);

        switch (_editTarget)
        {
            case "keyboard": _keyboard = inputValue; break;
            case "mouse": _mouse = inputValue; break;
            case "gamepad": _gamepad = inputValue; break;
            case "joystick1": _joystick1 = inputValue; break;
            case "joystick2": _joystick2 = inputValue; break;
        }

        UpdateFields();
        txtSelectedInput.Text = $"設定: {InputDisplayHelper.FormatInputJa(inputValue)}  ({inputValue})";
    }

    // 編集中の機器に合わない修飾キーのチェックは選べないようにする (無効にしたチェックは外す)。
    //   ゲームパッドの編集中はキーボード修飾 (L/R Shift・Ctrl・Alt) を無効、それ以外 (キーボード・マウス・ジョイスティック) は LB/RB を無効
    private void UpdateModifierAvailability(string target)
    {
        bool isGamepad = target == "gamepad";
        foreach (var chk in new[] { chkLShift, chkRShift, chkLCtrl, chkRCtrl, chkLAlt, chkRAlt })
        {
            chk.IsEnabled = !isGamepad;
            if (isGamepad) chk.IsChecked = false;
        }
        foreach (var chk in new[] { chkGpLB, chkGpRB })
        {
            chk.IsEnabled = isGamepad;
            if (!isGamepad) chk.IsChecked = false;
        }
    }

    // 修飾キーのチェックを付け、ゲームの形にする (例 "js1_rctrl+button3"、"gp1_shoulderl+a"、"kb1_lshift+w")。
    //   deviceType に合わない修飾は付けない (ゲームパッドにはキーボード修飾を、それ以外には LB/RB を付けない)
    private string AddCheckedModifiers(string inputValue, string deviceType)
    {
        bool isGamepad = deviceType == "gamepad";
        var modifiers = new List<string>();
        if (!isGamepad)
        {
            if (chkLShift.IsChecked == true) modifiers.Add("kb1_lshift");
            if (chkRShift.IsChecked == true) modifiers.Add("kb1_rshift");
            if (chkLCtrl.IsChecked == true) modifiers.Add("kb1_lctrl");
            if (chkRCtrl.IsChecked == true) modifiers.Add("kb1_rctrl");
            if (chkLAlt.IsChecked == true) modifiers.Add("kb1_lalt");
            if (chkRAlt.IsChecked == true) modifiers.Add("kb1_ralt");
        }
        else
        {
            if (chkGpLB.IsChecked == true) modifiers.Add("gp1_shoulderl");
            if (chkGpRB.IsChecked == true) modifiers.Add("gp1_shoulderr");
        }

        if (modifiers.Count > 0)
            inputValue = string.Join("+", modifiers) + "+" + inputValue;
        return InputCodeHelper.ToGameForm(inputValue, null);
    }

    // 確認欄に出している、js 番号を options から決められなかったジョイスティックの押下 (「この機器は js◯」を選んだら、これで出し直す)
    private GameControllerInputEventArgs? _jsChoicePress;

    // 物理コントローラー / HOTAS が押されたら確認欄を出す
    private void ControllerInput_Pressed(object? sender, GameControllerInputEventArgs e)
    {
        string deviceType;
        string deviceLabel;
        string baseValue;
        HideJsChoice();
        if (e.Kind == GameControllerKind.Gamepad)
        {
            deviceType = "gamepad";
            deviceLabel = "ゲームパッド";
            baseValue = $"gp1_{e.InputCode}";
        }
        else
        {
            if (!_data.TryResolveJsForDevice(e.VendorId, e.ProductId, e.DeviceId, out var js, out var manual))
            {
                _pendingPress = null;
                txtPressInput.Text = $"押された入力: {e.DisplayName} — {e.InputCode}";
                txtPressCurrent.Text = "どの js 番号か判別できません";
                txtPressOthers.Text = "";
                btnPressApply.IsEnabled = false;
                ShowJsChoice(e);
                pnlPressConfirm.Visibility = Visibility.Visible;
                return;
            }
            deviceType = $"joystick{js}";
            deviceLabel = manual
                ? $"HOTAS {_data.GetJsSideLabel(js)} (js{js}・手で選んだ番号)"
                : $"HOTAS {_data.GetJsSideLabel(js)} (js{js})";
            baseValue = $"js{js}_{e.InputCode}";
            // 手で選んだ番号のときは、選び直せるように選択の欄も出す
            if (manual) ShowJsChoice(e);
        }

        var value = AddCheckedModifiers(baseValue, deviceType);

        var current = deviceType switch
        {
            "gamepad" => _gamepad,
            "joystick1" => _joystick1,
            "joystick2" => _joystick2,
            _ => "",
        };

        var allActions = _data.Categories.SelectMany(c => c.Actions).ToList();
        var others = MouseLayoutPanel.FindBindingsForInput(allActions, value, deviceType)
            .Where(a => !ReferenceEquals(a, _original))
            .ToList();

        txtPressInput.Text = $"押された入力: {e.DisplayName} ({deviceLabel}) — {InputDisplayHelper.FormatInputJa(value)}  ({value})";
        // 割り当て解除 (接頭辞と空白だけ。"js1_ " など) は、ほかの表示と同じく「（解除）」だけを出す
        var currentCleared = !string.IsNullOrEmpty(current) &&
                             current.Split('+').All(p => InputCodeHelper.StripDevicePrefix(p).Length == 0);
        txtPressCurrent.Text = string.IsNullOrEmpty(current)
            ? $"この機能の{deviceLabel}の今の割り当て: なし"
            : currentCleared
                ? $"この機能の{deviceLabel}の今の割り当て: {InputDisplayHelper.FormatInputJa(current)}"
                : $"この機能の{deviceLabel}の今の割り当て: {InputDisplayHelper.FormatInputJa(current)}  ({current})";
        txtPressOthers.Text = others.Count == 0
            ? "この入力を使っている他の機能: なし"
            : "この入力を使っている他の機能:\n" + string.Join("\n", others.Select(a => $"・{a.DisplayName}  ({a.CategoryDisplayName})"));
        btnPressApply.IsEnabled = true;
        _pendingPress = (deviceType, value);
        pnlPressConfirm.Visibility = Visibility.Visible;
    }

    // [反映]: Key_Selected と同じく編集中のコピーに入れる (確定は従来どおり OK)
    private void PressApply_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingPress is not { } p) return;
        switch (p.DeviceType)
        {
            case "gamepad": _gamepad = p.Value; break;
            case "joystick1": _joystick1 = p.Value; break;
            case "joystick2": _joystick2 = p.Value; break;
        }
        UpdateFields();
        txtSelectedInput.Text = $"設定: {InputDisplayHelper.FormatInputJa(p.Value)}  ({p.Value})";
        HidePressConfirm();
    }

    // [キャンセル]: 何もせず確認欄を閉じる
    private void PressCancel_Click(object sender, RoutedEventArgs e) => HidePressConfirm();

    private void HidePressConfirm()
    {
        _pendingPress = null;
        HideJsChoice();
        pnlPressConfirm.Visibility = Visibility.Collapsed;
    }

    // 「この機器は js◯」の選択の欄を出す (ボタンには js 番号と HOTAS の左右を出す)
    private void ShowJsChoice(GameControllerInputEventArgs e)
    {
        _jsChoicePress = e;
        btnPressJs1.Content = $"js1 (HOTAS {_data.GetJsSideLabel(1)})";
        btnPressJs2.Content = $"js2 (HOTAS {_data.GetJsSideLabel(2)})";
        pnlPressJsChoice.Visibility = Visibility.Visible;
    }

    private void HideJsChoice()
    {
        _jsChoicePress = null;
        pnlPressJsChoice.Visibility = Visibility.Collapsed;
    }

    private void PressJs1_Click(object sender, RoutedEventArgs e) => ChooseJs(1);
    private void PressJs2_Click(object sender, RoutedEventArgs e) => ChooseJs(2);

    // 選んだ js 番号を覚えて (ActionMapData。保存フォルダごとの記録はエディターが行う)、同じ押下で確認欄を出し直す
    private void ChooseJs(int js)
    {
        if (_jsChoicePress is not { } press) return;
        _data.SetManualJsForDevice(press.VendorId, press.ProductId, press.DeviceId, js);
        ControllerInput_Pressed(this, press);
    }

    private void ResetDefault_Click(object sender, RoutedEventArgs e)
    {
        _keyboard = _original.DefaultKeyboard;
        _mouse = _original.DefaultMouse;
        _gamepad = _original.DefaultGamepad;
        _joystick1 = _original.DefaultJoystick1;
        _joystick2 = _original.DefaultJoystick2;
        UpdateFields();
    }

    // 割り当て解除: ゲームの解除の書き方 (機器の接頭辞 + 空白。"kb1_ "、"js1_ " など) で持つ (押した直後から「（解除）」と出る)
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        switch (_editTarget)
        {
            case "keyboard": _keyboard = "kb1_ "; break;
            case "mouse": _mouse = "mo1_ "; break;
            case "gamepad": _gamepad = "gp1_ "; break;
            case "joystick1": _joystick1 = "js1_ "; break;
            case "joystick2": _joystick2 = "js2_ "; break;
        }
        UpdateFields();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        _original.Keyboard = _keyboard;
        _original.Mouse = _mouse;
        _original.Gamepad = _gamepad;
        _original.Joystick1 = _joystick1;
        _original.Joystick2 = _joystick2;
        _original.IsLongPress = chkLongPress.IsChecked == true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

public class InputItem
{
    public string Display { get; set; }
    public string Value { get; set; }
    public InputItem(string display, string value) { Display = display; Value = value; }
}
