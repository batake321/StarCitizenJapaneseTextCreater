using System.Windows;
using System.Windows.Controls;

namespace StarCitizenJapaneseTextCreater;

public partial class BindingEditDialog : Window
{
    private readonly ActionBinding _original;
    private string _editTarget = "keyboard";

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

    // Logical joystick inputs (no js1_/js2_ prefix). Prefix is applied based on edit target.
    private static readonly List<InputItem> JoystickKeysBase = new()
    {
        new("Btn 1","button1"), new("Btn 2","button2"), new("Btn 3","button3"), new("Btn 4","button4"),
        new("Btn 5","button5"), new("Btn 6","button6"), new("Btn 7","button7"), new("Btn 8","button8"),
        new("Btn 9","button9"), new("Btn 10","button10"), new("Btn 11","button11"), new("Btn 12","button12"),
        new("Btn 13","button13"), new("Btn 14","button14"), new("Btn 15","button15"), new("Btn 16","button16"),
        new("Btn 17","button17"), new("Btn 18","button18"), new("Btn 19","button19"), new("Btn 20","button20"),
        new("Btn 21","button21"), new("Btn 22","button22"), new("Btn 23","button23"), new("Btn 24","button24"),
        new("Btn 25","button25"), new("Btn 26","button26"), new("Btn 27","button27"), new("Btn 28","button28"),
        new("Btn 29","button29"), new("Btn 30","button30"), new("Btn 31","button31"), new("Btn 32","button32"),
        new("X Axis","x"), new("Y Axis","y"), new("Z Axis","z"),
        new("RX","rotx"), new("RY","roty"), new("RZ","rotz"),
        new("Slider 1","slider1"), new("Slider 2","slider2"),
        new("Hat1 Up","hat1_up"), new("Hat1 Down","hat1_down"),
        new("Hat1 Left","hat1_left"), new("Hat1 Right","hat1_right"),
        new("Hat2 Up","hat2_up"), new("Hat2 Down","hat2_down"),
        new("Hat2 Left","hat2_left"), new("Hat2 Right","hat2_right"),
    };

    public BindingEditDialog(ActionBinding binding)
    {
        InitializeComponent();
        _original = binding;

        _keyboard = binding.Keyboard;
        _mouse = binding.Mouse;
        _gamepad = binding.Gamepad;
        _joystick1 = binding.Joystick1;
        _joystick2 = binding.Joystick2;
        _isLongPress = binding.IsLongPress;

        txtActionName.Text = $"{binding.DisplayName} ({binding.ActionName})";
        txtCategory.Text = ActionMapNames.GetCategoryName(binding.CategoryName);

        UpdateFields();
        ShowKeyList("keyboard");
    }

    private void UpdateFields()
    {
        txtKeyboard.Text = InputDisplayHelper.FormatInput(_keyboard);
        txtMouse.Text = InputDisplayHelper.FormatInput(_mouse);
        txtGamepad.Text = InputDisplayHelper.FormatInput(_gamepad);
        txtJoystickR.Text = InputDisplayHelper.FormatInput(_joystick1);
        txtJoystickL.Text = InputDisplayHelper.FormatInput(_joystick2);
        chkLongPress.IsChecked = _isLongPress;
    }

    private void ShowKeyList(string target)
    {
        _editTarget = target;
        grpSelector.Header = target switch
        {
            "keyboard" => "キーボード入力を選択",
            "mouse" => "マウス入力を選択",
            "gamepad" => "ゲームパッド入力を選択",
            "joystick1" => "HOTAS R 入力を選択 (VKB Gladiator NXT R)",
            "joystick2" => "HOTAS L 入力を選択 (VKB Gladiator NXT L)",
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

        var modifiers = new List<string>();
        if (chkLShift.IsChecked == true) modifiers.Add("kb1_lshift");
        if (chkRShift.IsChecked == true) modifiers.Add("kb1_rshift");
        if (chkLCtrl.IsChecked == true) modifiers.Add("kb1_lctrl");
        if (chkRCtrl.IsChecked == true) modifiers.Add("kb1_rctrl");
        if (chkLAlt.IsChecked == true) modifiers.Add("kb1_lalt");
        if (chkRAlt.IsChecked == true) modifiers.Add("kb1_ralt");
        if (chkGpLB.IsChecked == true) modifiers.Add("gp1_shoulderl");
        if (chkGpRB.IsChecked == true) modifiers.Add("gp1_shoulderr");

        var inputValue = _editTarget switch
        {
            "keyboard" => $"kb1_{item.Value}",
            "joystick1" => $"js1_{item.Value}",
            "joystick2" => $"js2_{item.Value}",
            _ => item.Value,
        };

        if (modifiers.Count > 0)
            inputValue = string.Join("+", modifiers) + "+" + inputValue;

        switch (_editTarget)
        {
            case "keyboard": _keyboard = inputValue; break;
            case "mouse": _mouse = inputValue; break;
            case "gamepad": _gamepad = inputValue; break;
            case "joystick1": _joystick1 = inputValue; break;
            case "joystick2": _joystick2 = inputValue; break;
        }

        UpdateFields();
        txtSelectedInput.Text = $"設定: {InputDisplayHelper.FormatInput(inputValue)}  ({inputValue})";
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

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        switch (_editTarget)
        {
            case "keyboard": _keyboard = ""; break;
            case "mouse": _mouse = ""; break;
            case "gamepad": _gamepad = ""; break;
            case "joystick1": _joystick1 = ""; break;
            case "joystick2": _joystick2 = ""; break;
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
