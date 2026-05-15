using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace StarCitizenJapaneseTextCreater;

public partial class KeyboardLayoutPanel : UserControl
{
    private ActionMapData _data = new();
    private readonly Dictionary<string, Border> _keyElements = new();
    private Action? _onBindingChanged;

    private const double U = 46;
    private const double Gap = 2;
    private const double KeyH = 38;

    public KeyboardLayoutPanel()
    {
        InitializeComponent();
    }

    public void SetData(ActionMapData data, Action? onBindingChanged)
    {
        _data = data;
        _onBindingChanged = onBindingChanged;
        BuildKeyboard();
        UpdateKeyColors();
    }

    private record KeyDef(double X, double Y, double W, double H, string Label, string Code, string? Code2 = null);

    private List<KeyDef> GetKeyLayout()
    {
        var keys = new List<KeyDef>();
        double x, y;

        // Row 0: Function keys
        y = 0;
        keys.Add(new(0, y, U, KeyH, "Esc", "escape"));
        x = U + Gap + U * 0.5;
        for (int i = 1; i <= 4; i++) { keys.Add(new(x, y, U, KeyH, $"F{i}", $"f{i}")); x += U + Gap; }
        x += U * 0.3;
        for (int i = 5; i <= 8; i++) { keys.Add(new(x, y, U, KeyH, $"F{i}", $"f{i}")); x += U + Gap; }
        x += U * 0.3;
        for (int i = 9; i <= 12; i++) { keys.Add(new(x, y, U, KeyH, $"F{i}", $"f{i}")); x += U + Gap; }
        x += U * 0.2;
        keys.Add(new(x, y, U, KeyH, "PrtSc", "print")); x += U + Gap;
        keys.Add(new(x, y, U, KeyH, "ScrLk", "scrolllock")); x += U + Gap;
        keys.Add(new(x, y, U, KeyH, "Pause", "pause"));

        // Row 1: Number row
        y = KeyH + Gap + 8;
        x = 0;
        keys.Add(new(x, y, U, KeyH, "半/全", "grave")); x += U + Gap;
        string[] numLabels = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0"];
        string[] numCodes = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0"];
        for (int i = 0; i < 10; i++) { keys.Add(new(x, y, U, KeyH, numLabels[i], numCodes[i])); x += U + Gap; }
        keys.Add(new(x, y, U, KeyH, "-", "minus")); x += U + Gap;
        keys.Add(new(x, y, U, KeyH, "^", "equals")); x += U + Gap;
        keys.Add(new(x, y, U, KeyH, "¥", "backslash")); x += U + Gap;
        keys.Add(new(x, y, U * 1.5, KeyH, "BS", "backspace")); x += U * 1.5 + Gap;
        x += U * 0.3;
        keys.Add(new(x, y, U, KeyH, "Ins", "insert")); x += U + Gap;
        keys.Add(new(x, y, U, KeyH, "Home", "home")); x += U + Gap;
        keys.Add(new(x, y, U, KeyH, "PgUp", "pgup"));

        // Row 2: QWERTY row
        y += KeyH + Gap;
        x = 0;
        keys.Add(new(x, y, U * 1.4, KeyH, "Tab", "tab")); x += U * 1.4 + Gap;
        string[] row2 = ["Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P"];
        foreach (var k in row2) { keys.Add(new(x, y, U, KeyH, k, k.ToLower())); x += U + Gap; }
        keys.Add(new(x, y, U, KeyH, "@", "lbracket")); x += U + Gap;
        keys.Add(new(x, y, U, KeyH, "[", "rbracket")); x += U + Gap;
        // Enter key (tall, spans 2 rows) - draw as single key in row 2
        keys.Add(new(x, y, U * 1.2, KeyH * 2 + Gap, "Enter", "enter")); // tall enter
        x = U * 1.4 + Gap + 12 * (U + Gap) + U * 1.2 + Gap + U * 0.3;
        keys.Add(new(x, y, U, KeyH, "Del", "delete")); x += U + Gap;
        keys.Add(new(x, y, U, KeyH, "End", "end")); x += U + Gap;
        keys.Add(new(x, y, U, KeyH, "PgDn", "pgdn"));

        // Row 3: Home row
        y += KeyH + Gap;
        x = 0;
        keys.Add(new(x, y, U * 1.6, KeyH, "CapsLk", "capslock")); x += U * 1.6 + Gap;
        string[] row3 = ["A", "S", "D", "F", "G", "H", "J", "K", "L"];
        foreach (var k in row3) { keys.Add(new(x, y, U, KeyH, k, k.ToLower())); x += U + Gap; }
        keys.Add(new(x, y, U, KeyH, ";", "semicolon")); x += U + Gap;
        keys.Add(new(x, y, U, KeyH, ":", "apostrophe")); x += U + Gap;
        keys.Add(new(x, y, U, KeyH, "]", "backslash2")); // ] key on JP keyboard

        // Row 4: Shift row
        y += KeyH + Gap;
        x = 0;
        keys.Add(new(x, y, U * 2.1, KeyH, "L-Shift", "lshift")); x += U * 2.1 + Gap;
        string[] row4 = ["Z", "X", "C", "V", "B", "N", "M"];
        foreach (var k in row4) { keys.Add(new(x, y, U, KeyH, k, k.ToLower())); x += U + Gap; }
        keys.Add(new(x, y, U, KeyH, ",", "comma")); x += U + Gap;
        keys.Add(new(x, y, U, KeyH, ".", "period")); x += U + Gap;
        keys.Add(new(x, y, U, KeyH, "/", "slash")); x += U + Gap;
        keys.Add(new(x, y, U, KeyH, "\\_", "backslash3")); x += U + Gap;
        keys.Add(new(x, y, U * 1.7, KeyH, "R-Shift", "rshift"));
        // Arrow up
        x = U * 1.4 + Gap + 12 * (U + Gap) + U * 1.2 + Gap + U * 0.3 + U + Gap;
        keys.Add(new(x, y, U, KeyH, "↑", "up"));

        // Row 5: Bottom row
        y += KeyH + Gap;
        x = 0;
        keys.Add(new(x, y, U * 1.3, KeyH, "L-Ctrl", "lctrl")); x += U * 1.3 + Gap;
        keys.Add(new(x, y, U, KeyH, "Win", "win")); x += U + Gap;
        keys.Add(new(x, y, U * 1.1, KeyH, "L-Alt", "lalt")); x += U * 1.1 + Gap;
        keys.Add(new(x, y, U * 1.1, KeyH, "無変換", "muhenkan")); x += U * 1.1 + Gap;
        keys.Add(new(x, y, U * 4, KeyH, "Space", "space")); x += U * 4 + Gap;
        keys.Add(new(x, y, U * 1.1, KeyH, "変換", "henkan")); x += U * 1.1 + Gap;
        keys.Add(new(x, y, U * 1.1, KeyH, "カナ", "kana")); x += U * 1.1 + Gap;
        keys.Add(new(x, y, U * 1.1, KeyH, "R-Alt", "ralt")); x += U * 1.1 + Gap;
        keys.Add(new(x, y, U, KeyH, "Win", "rwin")); x += U + Gap;
        keys.Add(new(x, y, U, KeyH, "App", "apps")); x += U + Gap;
        keys.Add(new(x, y, U * 1.3, KeyH, "R-Ctrl", "rctrl"));
        // Arrow keys
        x = U * 1.4 + Gap + 12 * (U + Gap) + U * 1.2 + Gap + U * 0.3;
        keys.Add(new(x, y, U, KeyH, "←", "left")); x += U + Gap;
        keys.Add(new(x, y, U, KeyH, "↓", "down")); x += U + Gap;
        keys.Add(new(x, y, U, KeyH, "→", "right"));

        // Numpad - positioned to the right of nav cluster
        var npX0 = U * 1.4 + Gap + 12 * (U + Gap) + U * 1.2 + Gap + U * 0.3 + 3 * (U + Gap) + U * 0.5;

        // Numpad Row 1: NumLk / * -
        y = KeyH + Gap + 8;
        x = npX0;
        keys.Add(new(x, y, U, KeyH, "NumLk", "numlock")); x += U + Gap;
        keys.Add(new(x, y, U, KeyH, "/", "np_divide")); x += U + Gap;
        keys.Add(new(x, y, U, KeyH, "*", "np_multiply")); x += U + Gap;
        keys.Add(new(x, y, U, KeyH, "-", "np_subtract"));

        // Numpad Row 2: 7 8 9 +
        y += KeyH + Gap;
        x = npX0;
        keys.Add(new(x, y, U, KeyH, "7", "np_7")); x += U + Gap;
        keys.Add(new(x, y, U, KeyH, "8", "np_8")); x += U + Gap;
        keys.Add(new(x, y, U, KeyH, "9", "np_9")); x += U + Gap;
        keys.Add(new(x, y, U, KeyH * 2 + Gap, "+", "np_add"));

        // Numpad Row 3: 4 5 6
        y += KeyH + Gap;
        x = npX0;
        keys.Add(new(x, y, U, KeyH, "4", "np_4")); x += U + Gap;
        keys.Add(new(x, y, U, KeyH, "5", "np_5")); x += U + Gap;
        keys.Add(new(x, y, U, KeyH, "6", "np_6"));

        // Numpad Row 4: 1 2 3 Enter
        y += KeyH + Gap;
        x = npX0;
        keys.Add(new(x, y, U, KeyH, "1", "np_1")); x += U + Gap;
        keys.Add(new(x, y, U, KeyH, "2", "np_2")); x += U + Gap;
        keys.Add(new(x, y, U, KeyH, "3", "np_3")); x += U + Gap;
        keys.Add(new(x, y, U, KeyH * 2 + Gap, "Ent", "np_enter"));

        // Numpad Row 5: 0 .
        y += KeyH + Gap;
        x = npX0;
        keys.Add(new(x, y, U * 2 + Gap, KeyH, "0", "np_0")); x += U * 2 + Gap + Gap;
        keys.Add(new(x, y, U, KeyH, ".", "np_period"));

        return keys;
    }

    private static readonly HashSet<string> ModifierCodes = new(StringComparer.OrdinalIgnoreCase)
        { "lshift", "rshift", "lctrl", "rctrl", "lalt", "ralt" };

    private string BuildModifierPrefix()
    {
        var parts = new List<string>();
        if (chkLShift.IsChecked == true) parts.Add("kb1_lshift");
        if (chkRShift.IsChecked == true) parts.Add("kb1_rshift");
        if (chkLCtrl.IsChecked == true) parts.Add("kb1_lctrl");
        if (chkRCtrl.IsChecked == true) parts.Add("kb1_rctrl");
        if (chkLAlt.IsChecked == true) parts.Add("kb1_lalt");
        if (chkRAlt.IsChecked == true) parts.Add("kb1_ralt");
        return parts.Count > 0 ? string.Join("+", parts) + "+" : "";
    }

    private void BuildKeyboard()
    {
        canvas.Children.Clear();
        _keyElements.Clear();

        foreach (var key in GetKeyLayout())
        {
            var border = new Border
            {
                Width = key.W,
                Height = key.H,
                Background = new SolidColorBrush(Color.FromRgb(240, 240, 240)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Cursor = Cursors.Hand,
                Tag = key.Code,
            };

            var textBlock = new TextBlock
            {
                Text = key.Label,
                FontSize = key.W < U * 1.2 ? 11 : 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };

            border.Child = textBlock;
            border.MouseEnter += Key_MouseEnter;
            border.MouseLeave += Key_MouseLeave;
            border.MouseLeftButtonDown += Key_Click;

            Canvas.SetLeft(border, key.X);
            Canvas.SetTop(border, key.Y);
            canvas.Children.Add(border);

            _keyElements[key.Code] = border;
        }
    }

    public void UpdateKeyColors()
    {
        if (_data.Categories.Count == 0) return;

        var modPrefix = BuildModifierPrefix();
        var allActions = _data.Categories.SelectMany(c => c.Actions).ToList();

        var activeModifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (chkLShift.IsChecked == true) activeModifiers.Add("lshift");
        if (chkRShift.IsChecked == true) activeModifiers.Add("rshift");
        if (chkLCtrl.IsChecked == true) activeModifiers.Add("lctrl");
        if (chkRCtrl.IsChecked == true) activeModifiers.Add("rctrl");
        if (chkLAlt.IsChecked == true) activeModifiers.Add("lalt");
        if (chkRAlt.IsChecked == true) activeModifiers.Add("ralt");

        foreach (var (code, border) in _keyElements)
        {
            var fullCode = modPrefix + "kb1_" + code;
            var bindings = FindBindingsForKey(allActions, fullCode);

            var isModifier = ModifierCodes.Contains(code);
            var isActiveModifier = isModifier && activeModifiers.Contains(code);

            // Determine dominant activation mode category for color
            var modeCategory = "tap";
            if (bindings.Count > 0)
            {
                var modes = bindings.Select(b => ActivationModeHelper.GetCategory(b.EffectiveKeyboardActivationMode)).ToList();
                if (modes.Contains("double_tap")) modeCategory = "double_tap";
                else if (modes.Contains("hold")) modeCategory = "hold";
                else if (modes.Contains("press")) modeCategory = "press";
            }

            Color bg, fg, borderColor;
            if (isActiveModifier)
            {
                bg = Color.FromRgb(255, 183, 77);
                fg = Colors.White;
                borderColor = Color.FromRgb(245, 124, 0);
            }
            else if (isModifier)
            {
                bg = Color.FromRgb(200, 200, 210);
                fg = Color.FromRgb(60, 60, 60);
                borderColor = Color.FromRgb(150, 150, 160);
            }
            else if (bindings.Count > 0)
            {
                (bg, fg, borderColor) = modeCategory switch
                {
                    "hold" => (Color.FromRgb(171, 71, 188), Colors.White, Color.FromRgb(142, 36, 170)),
                    "double_tap" => (Color.FromRgb(38, 166, 154), Colors.White, Color.FromRgb(0, 137, 123)),
                    _ => (Color.FromRgb(66, 165, 245), Colors.White, Color.FromRgb(30, 136, 229)),
                };
            }
            else
            {
                bg = Color.FromRgb(240, 240, 240);
                fg = Color.FromRgb(80, 80, 80);
                borderColor = Color.FromRgb(180, 180, 180);
            }

            border.Background = new SolidColorBrush(bg);
            border.BorderBrush = new SolidColorBrush(borderColor);
            if (border.Child is TextBlock tb)
                tb.Foreground = new SolidColorBrush(fg);

            if (bindings.Count > 0)
            {
                var sp = new StackPanel { MaxWidth = 350 };
                sp.Children.Add(new TextBlock
                {
                    Text = InputDisplayHelper.FormatInput(fullCode),
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 0, 0, 4)
                });
                foreach (var b in bindings)
                {
                    var actMode = b.EffectiveKeyboardActivationMode;
                    var modeDisplay = ActivationModeHelper.GetDisplayName(actMode);
                    var modeTag = string.IsNullOrEmpty(modeDisplay) ? "" : $"  [{modeDisplay}]";

                    sp.Children.Add(new TextBlock
                    {
                        Text = $"• {b.DisplayName}{modeTag}",
                        TextWrapping = TextWrapping.Wrap
                    });
                    sp.Children.Add(new TextBlock
                    {
                        Text = $"   {ActionMapNames.GetCategoryName(b.CategoryName)}",
                        Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120)),
                        FontSize = 11,
                        Margin = new Thickness(0, 0, 0, 2)
                    });
                }
                border.ToolTip = new ToolTip { Content = sp };
            }
            else
            {
                border.ToolTip = null;
            }
        }

        var totalBound = _keyElements.Keys
            .Where(c => !ModifierCodes.Contains(c))
            .Count(c => FindBindingsForKey(allActions, modPrefix + "kb1_" + c).Count > 0);
        var modText = string.IsNullOrEmpty(modPrefix) ? "修飾キーなし" : modPrefix.Replace("kb1_", "").TrimEnd('+').Replace("+", " + ");
        txtKeyInfo.Text = $"{modText} — {totalBound} キーにバインド済み";
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

    private static List<ActionBinding> FindBindingsForKey(List<ActionBinding> allActions, string fullCode)
    {
        var target = NormalizeKeyCode(fullCode);
        return allActions
            .Where(a => !string.IsNullOrEmpty(a.Keyboard) &&
                        NormalizeKeyCode(a.Keyboard) == target)
            .ToList();
    }

    private void Key_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Border border) return;
        border.BorderThickness = new Thickness(2);
    }

    private void Key_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not Border border) return;
        border.BorderThickness = new Thickness(1);
    }

    private void Key_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.Tag is not string code) return;
        if (ModifierCodes.Contains(code)) return;

        var modPrefix = BuildModifierPrefix();
        var fullCode = modPrefix + "kb1_" + code;
        var allActions = _data.Categories.SelectMany(c => c.Actions).ToList();
        var bindings = FindBindingsForKey(allActions, fullCode);

        var displayKey = InputDisplayHelper.FormatInput(fullCode);
        var dlg = new KeyAssignDialog(_data, fullCode, displayKey, bindings)
        {
            Owner = Window.GetWindow(this)
        };
        if (dlg.ShowDialog() == true)
        {
            UpdateKeyColors();
            _onBindingChanged?.Invoke();
        }
    }

    private void Modifier_Changed(object sender, RoutedEventArgs e) => UpdateKeyColors();
}
