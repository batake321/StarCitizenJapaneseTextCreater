using System.Windows;
using System.Windows.Controls;

namespace StarCitizenJapaneseTextCreater;

public partial class BindingEditDialog : Window
{
    private readonly ActionBinding _binding;
    private string _editTarget = "keyboard";

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
        new("A","gp1_btn_a"), new("B","gp1_btn_b"), new("X","gp1_btn_x"), new("Y","gp1_btn_y"),
        new("LB","gp1_shoulderl"), new("RB","gp1_shoulderr"),
        new("LT","gp1_triggerl_btn"), new("RT","gp1_triggerr_btn"),
        new("L Stick Press","gp1_thumbl"), new("R Stick Press","gp1_thumbr"),
        new("L Stick X","gp1_thumblx"), new("L Stick Y","gp1_thumbly"),
        new("R Stick X","gp1_thumbrx"), new("R Stick Y","gp1_thumbry"),
        new("D-Pad Up","gp1_dpad_up"), new("D-Pad Down","gp1_dpad_down"),
        new("D-Pad Left","gp1_dpad_left"), new("D-Pad Right","gp1_dpad_right"),
        new("Start","gp1_start"), new("Back","gp1_back"),
    };

    private static readonly List<InputItem> JoystickKeys = new()
    {
        new("Btn 1","js1_button1"), new("Btn 2","js1_button2"), new("Btn 3","js1_button3"),
        new("Btn 4","js1_button4"), new("Btn 5","js1_button5"), new("Btn 6","js1_button6"),
        new("Btn 7","js1_button7"), new("Btn 8","js1_button8"),
        new("X Axis","js1_x"), new("Y Axis","js1_y"), new("Z Axis","js1_z"),
        new("RX","js1_rotx"), new("RY","js1_roty"), new("RZ","js1_rotz"),
        new("Slider 1","js1_slider1"), new("Slider 2","js1_slider2"),
        new("Hat Up","js1_hat1_up"), new("Hat Down","js1_hat1_down"),
        new("Hat Left","js1_hat1_left"), new("Hat Right","js1_hat1_right"),
    };

    public BindingEditDialog(ActionBinding binding)
    {
        InitializeComponent();
        _binding = binding;

        txtActionName.Text = $"{binding.DisplayName} ({binding.ActionName})";
        txtCategory.Text = ActionMapNames.GetCategoryName(binding.CategoryName);

        UpdateFields();
        ShowKeyList("keyboard");
    }

    private void UpdateFields()
    {
        txtKeyboard.Text = _binding.KeyboardDisplay;
        txtMouse.Text = _binding.MouseDisplay;
        txtGamepad.Text = _binding.GamepadDisplay;
        txtJoystick.Text = _binding.JoystickDisplay;
        chkLongPress.IsChecked = _binding.IsLongPress;
    }

    private void ShowKeyList(string target)
    {
        _editTarget = target;
        grpSelector.Header = target switch
        {
            "keyboard" => "キーボード入力を選択",
            "mouse" => "マウス入力を選択",
            "gamepad" => "ゲームパッド入力を選択",
            "joystick" => "ジョイスティック入力を選択",
            _ => "入力を選択"
        };

        lstKeys.ItemsSource = target switch
        {
            "keyboard" => KeyboardKeys,
            "mouse" => MouseKeys,
            "gamepad" => GamepadKeys,
            "joystick" => JoystickKeys,
            _ => KeyboardKeys
        };
    }

    private void SetKeyboard_Click(object sender, RoutedEventArgs e) => ShowKeyList("keyboard");
    private void SetMouse_Click(object sender, RoutedEventArgs e) => ShowKeyList("mouse");
    private void SetGamepad_Click(object sender, RoutedEventArgs e) => ShowKeyList("gamepad");
    private void SetJoystick_Click(object sender, RoutedEventArgs e) => ShowKeyList("joystick");

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

        var inputValue = _editTarget == "keyboard" ? $"kb1_{item.Value}" : item.Value;

        if (modifiers.Count > 0)
            inputValue = string.Join("+", modifiers) + "+" + inputValue;

        switch (_editTarget)
        {
            case "keyboard": _binding.Keyboard = inputValue; break;
            case "mouse": _binding.Mouse = inputValue; break;
            case "gamepad": _binding.Gamepad = inputValue; break;
            case "joystick": _binding.Joystick = inputValue; break;
        }

        UpdateFields();
        txtSelectedInput.Text = $"設定: {InputDisplayHelper.FormatInput(inputValue)}  ({inputValue})";
    }

    private void ResetDefault_Click(object sender, RoutedEventArgs e)
    {
        _binding.Keyboard = _binding.DefaultKeyboard;
        _binding.Mouse = _binding.DefaultMouse;
        _binding.Gamepad = _binding.DefaultGamepad;
        _binding.Joystick = _binding.DefaultJoystick;
        UpdateFields();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        switch (_editTarget)
        {
            case "keyboard": _binding.Keyboard = ""; break;
            case "mouse": _binding.Mouse = ""; break;
            case "gamepad": _binding.Gamepad = ""; break;
            case "joystick": _binding.Joystick = ""; break;
        }
        UpdateFields();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        _binding.IsLongPress = chkLongPress.IsChecked == true;
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
