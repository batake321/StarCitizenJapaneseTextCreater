using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace StarCitizenJapaneseTextCreater;

public partial class BindingEditDialog : Window
{
    private readonly ActionBinding _original;
    private readonly ActionMapData _data;   // js 番号の対応と、他の機能の一覧に使う (binding.Owner)
    // 選んでいる機器の欄 ("keyboard" / "mouse" / "gamepad" / "joystick1" / "joystick2")。"" = まだ選んでいない
    //   (開いた直後は選んでいない。欄をクリックするか［設定］を押すと選ぶ)
    private string _editTarget = "";

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
        ShowKeyList("");   // 開いた直後は、どの欄も選んでいない (［クリア］は押せない)

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
        UpdateSelectedInputText();
    }

    // 選んでいる機器 (_editTarget) の今の値 (編集中のコピー)
    private string CurrentTargetValue() => _editTarget switch
    {
        "keyboard" => _keyboard,
        "mouse" => _mouse,
        "gamepad" => _gamepad,
        "joystick1" => _joystick1,
        "joystick2" => _joystick2,
        _ => "",
    };

    // 下の枠の「設定: …」の行: 選んでいる機器の今の値を出す (割り当てが無い・解除したときは「なし」。欄を選んでいなければ出さない)
    private void UpdateSelectedInputText()
    {
        if (_editTarget.Length == 0)
        {
            txtSelectedInput.Text = "";
            return;
        }
        var v = CurrentTargetValue();
        var ja = InputDisplayHelper.FormatInputJa(v);
        txtSelectedInput.Text = ja.Length > 0 ? $"設定: {ja}  ({v})" : "設定: なし";
    }

    // 選んでいる欄に赤い枠を付ける (選んでいない欄は元の見た目)。［クリア］は欄を選んでいるときだけ押せる
    private void UpdateTargetHighlight()
    {
        var rows = new (string Target, TextBox Box)[]
        {
            ("keyboard", txtKeyboard),
            ("mouse", txtMouse),
            ("gamepad", txtGamepad),
            ("joystick1", txtJoystickR),
            ("joystick2", txtJoystickL),
        };
        foreach (var (target, box) in rows)
        {
            if (target == _editTarget)
            {
                box.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(229, 57, 53));
                box.BorderThickness = new Thickness(3);
            }
            else
            {
                box.ClearValue(Control.BorderBrushProperty);
                box.ClearValue(Control.BorderThicknessProperty);
            }
        }
        btnClear.IsEnabled = _editTarget.Length > 0;
    }

    // 欄をクリック (または Tab で移る) して選ぶ: その欄の［設定］を押したときと同じ
    private void Field_GotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is string target && target != _editTarget)
            ShowKeyList(target);
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
            _ => "入力を選択 (上の「現在のバインド」の欄をクリックして、機器を選んでください)"
        };

        lstKeys.ItemsSource = target switch
        {
            "keyboard" => KeyboardKeys,
            "mouse" => MouseKeys,
            "gamepad" => GamepadKeys,
            "joystick1" => JoystickKeysBase,
            "joystick2" => JoystickKeysBase,
            _ => null,   // 欄を選んでいないときは一覧を出さない
        };
        // 前の機器で選んだ項目の選択を外す (js1 と js2 は同じ一覧なので、外さないと前の選択が残り、同じ項目を選び直せない)
        lstKeys.SelectedItem = null;
        UpdateSelectedInputText();
        UpdateTargetHighlight();
    }

    private void SetKeyboard_Click(object sender, RoutedEventArgs e) => ShowKeyList("keyboard");
    private void SetMouse_Click(object sender, RoutedEventArgs e) => ShowKeyList("mouse");
    private void SetGamepad_Click(object sender, RoutedEventArgs e) => ShowKeyList("gamepad");
    private void SetJoystickR_Click(object sender, RoutedEventArgs e) => ShowKeyList("joystick1");
    private void SetJoystickL_Click(object sender, RoutedEventArgs e) => ShowKeyList("joystick2");

    private void Key_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (lstKeys.SelectedItem is not InputItem item || _editTarget.Length == 0) return;

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

        UpdateFields();   // 下の枠の「設定: …」も、選んでいる機器の今の値で出し直す
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
        // 機器の名前: js 番号が分かっていれば actionmaps.xml の options の名前 (Product から GUID を除いたもの)、無ければ Windows の名前
        var deviceName = e.DisplayName;
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
                ClearPressOthers();
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
            var optionName = _data.GetJsProductName(js);
            if (!string.IsNullOrEmpty(optionName)) deviceName = optionName;
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

        txtPressInput.Text = $"押された入力: {deviceName} ({deviceLabel}) — {InputDisplayHelper.FormatInputJa(value)}  ({value})";
        // 割り当て解除 (接頭辞と空白だけ。"js1_ " など) は、割り当てが無いときと同じく「なし」と出す (「（解除）」は出さない)
        var currentCleared = !string.IsNullOrEmpty(current) &&
                             current.Split('+').All(p => InputCodeHelper.StripDevicePrefix(p).Length == 0);
        txtPressCurrent.Text = string.IsNullOrEmpty(current) || currentCleared
            ? $"この機能の{deviceLabel}の今の割り当て: なし"
            : $"この機能の{deviceLabel}の今の割り当て: {InputDisplayHelper.FormatInputJa(current)}  ({current})";
        _lastOthers = others;
        _lastOthersDevice = deviceType;
        RenderPressOthers();
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
        UpdateFields();   // 「設定: …」は選んでいる機器の今の値 (押した機器が選んでいる機器と違っても、その機器の値は出さない)
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

    // 割り当て解除: ゲームの解除の書き方 (機器の接頭辞 + 空白。"kb1_ "、"js1_ " など) で持つ (画面には空で出る。保存するとこの書き方でファイルに書く)
    //   選んでいる欄 (赤い枠) の割り当てを消す。欄を選んでいなければ何もしない (［クリア］は押せない)
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_editTarget.Length == 0) return;
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

    // ===== この入力を使っている他の機能の［変更］［削除］ =====
    //   その場では変えず、予定として持つ (行に「→ 削除」「→ ボタン5 に変更」と出す)。［OK］で確定し、［キャンセル］では捨てる。
    //   予定は行の［取消］で取り消せる。確認欄を閉じても予定は残る (下に件数を出す)
    private readonly Dictionary<(ActionBinding Action, string DeviceType), string> _otherPlans = new();
    private List<ActionBinding> _lastOthers = new();
    private string _lastOthersDevice = "";

    private static string GetDeviceValue(ActionBinding a, string deviceType) => deviceType switch
    {
        "gamepad" => a.Gamepad,
        "joystick1" => a.Joystick1,
        "joystick2" => a.Joystick2,
        _ => "",
    };

    private static void SetDeviceValue(ActionBinding a, string deviceType, string value)
    {
        switch (deviceType)
        {
            case "gamepad": a.Gamepad = value; break;
            case "joystick1": a.Joystick1 = value; break;
            case "joystick2": a.Joystick2 = value; break;
        }
    }

    // 解除の書き方 (機器の接頭辞 + 空白)
    private static string ClearValueFor(string deviceType) => deviceType switch
    {
        "gamepad" => "gp1_ ",
        "joystick1" => "js1_ ",
        "joystick2" => "js2_ ",
        _ => "",
    };

    private void ClearPressOthers()
    {
        _lastOthers = new();
        _lastOthersDevice = "";
        txtPressOthers.Text = "";
        pnlPressOthers.Children.Clear();
    }

    private void RenderPressOthers()
    {
        pnlPressOthers.Children.Clear();
        txtPressOthers.Text = _lastOthers.Count == 0 ? "この入力を使っている他の機能: なし" : "この入力を使っている他の機能:";
        foreach (var a in _lastOthers)
        {
            var key = (a, _lastOthersDevice);
            var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            text.Inlines.Add(new System.Windows.Documents.Run($"・{a.DisplayName}  ({a.CategoryDisplayName})"));
            bool planned = _otherPlans.TryGetValue(key, out var plan);
            if (planned)
            {
                var planText = InputCodeHelper.IsAssigned(plan) ? $"  → {InputDisplayHelper.FormatInputJa(plan ?? "")} に変更" : "  → 削除";
                text.Inlines.Add(new System.Windows.Documents.Run(planText)
                {
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(216, 67, 21)),
                    FontWeight = FontWeights.Bold,
                });
            }
            Grid.SetColumn(text, 0);
            row.Children.Add(text);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };
            var btnChange = new Button { Content = "変更", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 4, 0) };
            btnChange.Click += (_, _) => ChangeOther(a, _lastOthersDevice);
            var btnDelete = new Button { Content = "削除", Padding = new Thickness(8, 1, 8, 1), Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(211, 47, 47)) };
            btnDelete.Click += (_, _) => { _otherPlans[(a, _lastOthersDevice)] = ClearValueFor(_lastOthersDevice); RenderPressOthers(); };
            buttons.Children.Add(btnChange);
            buttons.Children.Add(btnDelete);
            if (planned)
            {
                var btnUndo = new Button { Content = "取消", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(4, 0, 0, 0), ToolTip = "この機能の変更・削除の予定を取り消します" };
                btnUndo.Click += (_, _) => { _otherPlans.Remove((a, _lastOthersDevice)); RenderPressOthers(); };
                buttons.Children.Add(btnUndo);
            }
            Grid.SetColumn(buttons, 1);
            row.Children.Add(buttons);
            pnlPressOthers.Children.Add(row);
        }
        UpdateOtherPlansText();
    }

    private void UpdateOtherPlansText()
    {
        if (_otherPlans.Count == 0)
        {
            txtOtherPlans.Visibility = Visibility.Collapsed;
            return;
        }
        txtOtherPlans.Text = $"ほかの機能の変更・削除の予定: {_otherPlans.Count} 件 (［OK］で確定し、［キャンセル］で取り消します)";
        txtOtherPlans.Visibility = Visibility.Visible;
    }

    // ［変更］: 割り当てるボタンを押してもらう窓を開く。［反映］なら予定にする (今の割り当てと同じなら予定を外す)
    private void ChangeOther(ActionBinding a, string deviceType)
    {
        var original = GetDeviceValue(a, deviceType);
        // この窓の押下の待ち受けは止める (変更の窓が自分で待ち受ける。閉じたら、その後に押した入力から再び受ける)
        _controllerInput?.Stop();
        bool? result;
        string? newValue;
        try
        {
            var dlg = new ReassignInputDialog(_data, a, deviceType, original) { Owner = this };
            result = dlg.ShowDialog();
            newValue = dlg.NewValue;
        }
        finally
        {
            try { _controllerInput?.Start(); } catch (Exception) { }
        }
        if (result != true || newValue == null) return;
        if (newValue == original) _otherPlans.Remove((a, deviceType));
        else _otherPlans[(a, deviceType)] = newValue;
        RenderPressOthers();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        // ほかの機能の［変更］［削除］の予定を確定する
        foreach (var ((action, deviceType), value) in _otherPlans)
            SetDeviceValue(action, deviceType, value);
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

// 「割り当ての変更」の窓: ほかの機能の割り当てを、押したボタン (同じ機器) に変える。
//   押したボタン・ハットと、その入力が割り当て済みの機能を出す。［反映］で NewValue を返す (修飾キーは元の割り当てのものを引き継ぐ)。
//   押下の待ち受けはバインディング編集と同じ GameControllerInput。js 番号を決められない機器は「この機器は: js1 / js2」で選ぶ
public sealed class ReassignInputDialog : Window
{
    private readonly ActionMapData _data;
    private readonly ActionBinding _target;
    private readonly string _deviceType;     // "gamepad" / "joystick1" / "joystick2"
    private readonly List<string> _modifiers; // 元の割り当ての修飾キー (接頭辞なしの部分。"rctrl"、"shoulderl" など)
    private GameControllerInput? _input;
    private GameControllerInputEventArgs? _choicePress;

    private readonly TextBlock _txtPressed;
    private readonly TextBlock _txtAssigned;
    private readonly TextBlock _txtWarn;
    private readonly WrapPanel _pnlChoice;
    private readonly Button _btnChoice1;
    private readonly Button _btnChoice2;
    private readonly Button _btnApply;
    private string? _pendingValue;

    // ［反映］で閉じたときの新しい割り当て (ゲームの形)。それ以外は null
    public string? NewValue { get; private set; }

    public ReassignInputDialog(ActionMapData data, ActionBinding target, string deviceType, string originalValue)
    {
        _data = data;
        _target = target;
        _deviceType = deviceType;
        var bodies = (originalValue ?? "").Split('+').Select(InputCodeHelper.StripDevicePrefix).Where(x => x.Length > 0).ToList();
        _modifiers = InputCodeHelper.IsAssigned(originalValue) ? bodies.Take(bodies.Count - 1).ToList() : bodies;

        Title = "割り当ての変更";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new StackPanel { Margin = new Thickness(16), MinWidth = 420, MaxWidth = 560 };
        root.Children.Add(new TextBlock
        {
            Text = $"「{target.DisplayName}」を割り当てるボタンを押してください。",
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
        });
        var origJa = InputDisplayHelper.FormatInputJa(originalValue ?? "");
        root.Children.Add(new TextBlock
        {
            Text = $"機器: {DeviceLabel()}    今の割り当て: {(origJa.Length > 0 ? origJa : "なし")}" +
                   (_modifiers.Count > 0 ? $"    (修飾キー {string.Join(" + ", _modifiers.Select(m => InputDisplayHelper.FormatInputJa(Prefix + m)))} は引き継ぎます)" : ""),
            Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });

        _pnlChoice = new WrapPanel { Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        _pnlChoice.Children.Add(new TextBlock { Text = "どの js 番号か判別できません。この機器は:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        _btnChoice1 = new Button { Content = "js1", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0) };
        _btnChoice1.Click += (_, _) => ChooseJs(1);
        _btnChoice2 = new Button { Content = "js2", Padding = new Thickness(8, 2, 8, 2) };
        _btnChoice2.Click += (_, _) => ChooseJs(2);
        _pnlChoice.Children.Add(_btnChoice1);
        _pnlChoice.Children.Add(_btnChoice2);
        root.Children.Add(_pnlChoice);

        _txtWarn = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(211, 47, 47)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        root.Children.Add(_txtWarn);

        var box = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(255, 253, 231)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(251, 192, 45)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 10, 0, 0),
        };
        var boxPanel = new StackPanel();
        _txtPressed = new TextBlock { Text = "(まだ押されていません)", FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap };
        _txtAssigned = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        boxPanel.Children.Add(_txtPressed);
        boxPanel.Children.Add(new ScrollViewer { MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _txtAssigned });
        box.Child = boxPanel;
        root.Children.Add(box);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        _btnApply = new Button { Content = "反映", Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false, FontWeight = FontWeights.Bold };
        _btnApply.Click += (_, _) =>
        {
            if (_pendingValue == null) return;
            NewValue = _pendingValue;
            DialogResult = true;
        };
        var btnCancel = new Button { Content = "中止", Padding = new Thickness(12, 4, 12, 4), IsCancel = true };
        buttons.Children.Add(_btnApply);
        buttons.Children.Add(btnCancel);
        root.Children.Add(buttons);
        Content = root;

        Loaded += (_, _) =>
        {
            try
            {
                _input = new GameControllerInput();
                _input.InputPressed += Input_Pressed;
                _input.Start();
            }
            catch (Exception)
            {
                _input?.Dispose();
                _input = null;
                _txtWarn.Text = "コントローラーの入力を受け取れません。［中止］で閉じてください。";
                _txtWarn.Visibility = Visibility.Visible;
            }
        };
        Closed += (_, _) =>
        {
            if (_input == null) return;
            _input.InputPressed -= Input_Pressed;
            _input.Dispose();
            _input = null;
        };
    }

    private int TargetJs => _deviceType == "joystick2" ? 2 : 1;
    private string Prefix => _deviceType switch { "gamepad" => "gp1_", "joystick2" => "js2_", _ => "js1_" };

    private string DeviceLabel() => _deviceType == "gamepad"
        ? "ゲームパッド"
        : $"HOTAS {_data.GetJsSideLabel(TargetJs)} (js{TargetJs})";

    private void Input_Pressed(object? sender, GameControllerInputEventArgs e)
    {
        _txtWarn.Visibility = Visibility.Collapsed;
        string deviceName = e.DisplayName;
        if (_deviceType == "gamepad")
        {
            if (e.Kind != GameControllerKind.Gamepad)
            {
                ShowWarn($"別の機器の入力です。{DeviceLabel()} のボタンを押してください。");
                return;
            }
        }
        else
        {
            if (e.Kind != GameControllerKind.Joystick)
            {
                ShowWarn($"別の機器の入力です。{DeviceLabel()} のボタンを押してください。");
                return;
            }
            if (!_data.TryResolveJsForDevice(e.VendorId, e.ProductId, e.DeviceId, out var js, out _))
            {
                _choicePress = e;
                _btnChoice1.Content = $"js1 (HOTAS {_data.GetJsSideLabel(1)})";
                _btnChoice2.Content = $"js2 (HOTAS {_data.GetJsSideLabel(2)})";
                _pnlChoice.Visibility = Visibility.Visible;
                return;
            }
            _pnlChoice.Visibility = Visibility.Collapsed;
            if (js != TargetJs)
            {
                ShowWarn($"別のスティック (HOTAS {_data.GetJsSideLabel(js)} (js{js})) の入力です。{DeviceLabel()} のボタンを押してください。");
                return;
            }
            var optionName = _data.GetJsProductName(js);
            if (!string.IsNullOrEmpty(optionName)) deviceName = optionName;
        }

        var value = InputCodeHelper.ToGameForm(Prefix + string.Join("+", _modifiers.Append(e.InputCode)), Prefix);
        _pendingValue = value;
        _btnApply.IsEnabled = true;
        _txtPressed.Text = $"{deviceName} — {InputDisplayHelper.FormatInputJa(value)} ({value})";
        var allActions = _data.Categories.SelectMany(c => c.Actions).ToList();
        var assigned = MouseLayoutPanel.FindBindingsForInput(allActions, value, _deviceType)
            .Where(a => !ReferenceEquals(a, _target))
            .ToList();
        _txtAssigned.Text = assigned.Count == 0
            ? "割り当て済みの機能: なし"
            : "割り当て済みの機能:\n" + string.Join("\n", assigned.Select(a => $"・{a.DisplayName}  ({a.CategoryDisplayName})"));
    }

    private void ShowWarn(string message)
    {
        _txtWarn.Text = message;
        _txtWarn.Visibility = Visibility.Visible;
    }

    // 選んだ番号を覚えて (ActionMapData。保存はエディターが行う)、同じ押下で続ける
    private void ChooseJs(int js)
    {
        if (_choicePress is not { } press) return;
        _data.SetManualJsForDevice(press.VendorId, press.ProductId, press.DeviceId, js);
        _pnlChoice.Visibility = Visibility.Collapsed;
        Input_Pressed(this, press);
    }
}
