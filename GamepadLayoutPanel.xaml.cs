using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace StarCitizenJapaneseTextCreater;

public partial class GamepadLayoutPanel : UserControl
{
    private ActionMapData _data = new();
    private readonly Dictionary<string, Border> _buttonElements = new();
    private Action? _onBindingChanged;

    private static readonly HashSet<string> ModifierCodes = new(StringComparer.OrdinalIgnoreCase)
        { "gp1_shoulderl", "gp1_shoulderr" };

    public GamepadLayoutPanel()
    {
        InitializeComponent();
    }

    public void SetData(ActionMapData data, Action? onBindingChanged)
    {
        _data = data;
        _onBindingChanged = onBindingChanged;
        BuildLayout();
        UpdateColors();
    }

    private record ButtonDef(double X, double Y, double W, double H, string Label, string Code);

    private string BuildModifierPrefix()
    {
        var parts = new List<string>();
        if (chkLB.IsChecked == true) parts.Add("gp1_shoulderl");
        if (chkRB.IsChecked == true) parts.Add("gp1_shoulderr");
        return parts.Count > 0 ? string.Join("+", parts) + "+" : "";
    }

    private void BuildLayout()
    {
        canvas.Children.Clear();
        _buttonElements.Clear();

        double cx = 350, cy = 160;
        double padW = 500, padH = 300;

        var padOutline = new Border
        {
            Width = padW,
            Height = padH,
            Background = new SolidColorBrush(Color.FromRgb(245, 245, 245)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(60),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(padOutline, cx - padW / 2);
        Canvas.SetTop(padOutline, cy - padH / 2);
        canvas.Children.Add(padOutline);

        var label = new TextBlock
        {
            Text = "Xbox Controller",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
            FontWeight = FontWeights.Bold
        };
        Canvas.SetLeft(label, cx - 50);
        Canvas.SetTop(label, cy - 8);
        canvas.Children.Add(label);

        double bw = 60, bh = 36;
        double stickW = 70, stickH = 70;

        var buttons = new List<ButtonDef>
        {
            // Bumpers and triggers
            new(cx - 180, cy - padH / 2 - 30, 80, bh, "LB", "gp1_shoulderl"),
            new(cx + 100, cy - padH / 2 - 30, 80, bh, "RB", "gp1_shoulderr"),
            new(cx - 180, cy - padH / 2 - 70, 80, bh, "LT", "gp1_triggerl_btn"),
            new(cx + 100, cy - padH / 2 - 70, 80, bh, "RT", "gp1_triggerr_btn"),

            // Face buttons (ABXY) - diamond layout
            new(cx + 140, cy - 50, bw, bh, "Y", "gp1_y"),
            new(cx + 100, cy - 14, bw, bh, "X", "gp1_x"),
            new(cx + 180, cy - 14, bw, bh, "B", "gp1_b"),
            new(cx + 140, cy + 22, bw, bh, "A", "gp1_a"),

            // D-Pad - diamond layout
            new(cx - 200, cy + 20, 50, 32, "↑", "gp1_dpad_up"),
            new(cx - 240, cy + 56, 50, 32, "←", "gp1_dpad_left"),
            new(cx - 160, cy + 56, 50, 32, "→", "gp1_dpad_right"),
            new(cx - 200, cy + 92, 50, 32, "↓", "gp1_dpad_down"),

            // Center buttons
            new(cx - 50, cy - 50, bw, 30, "Back", "gp1_back"),
            new(cx + 10, cy - 50, bw, 30, "Start", "gp1_start"),

            // Left stick
            new(cx - 180, cy - 80, stickW, stickH, "L Stick\nPress", "gp1_thumbl"),
            // Right stick
            new(cx + 60, cy + 50, stickW, stickH, "R Stick\nPress", "gp1_thumbr"),
        };

        foreach (var btn in buttons)
        {
            var border = CreateButton(btn, btn.Code.Contains("thumb") ? 35 : 6);
            _buttonElements[btn.Code] = border;
        }

        // Stick axes below the controller
        double axisY = cy + padH / 2 + 30;
        var axisButtons = new List<ButtonDef>
        {
            new(cx - 220, axisY, 80, 36, "L Stick X\n← →", "gp1_thumblx"),
            new(cx - 130, axisY, 80, 36, "L Stick Y\n↑ ↓", "gp1_thumbly"),
            new(cx + 50, axisY, 80, 36, "R Stick X\n← →", "gp1_thumbrx"),
            new(cx + 140, axisY, 80, 36, "R Stick Y\n↑ ↓", "gp1_thumbry"),
        };
        foreach (var btn in axisButtons)
        {
            var border = CreateButton(btn, 6);
            _buttonElements[btn.Code] = border;
        }

        // Binding list
        _bindingListX = cx + padW / 2 + 40;
        _bindingListY = cy - padH / 2;
        var titleBlock = new TextBlock
        {
            Text = "ゲームパッドバインド一覧",
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(51, 51, 51))
        };
        Canvas.SetLeft(titleBlock, _bindingListX);
        Canvas.SetTop(titleBlock, _bindingListY - 24);
        canvas.Children.Add(titleBlock);
    }

    private double _bindingListX;
    private double _bindingListY;

    private Border CreateButton(ButtonDef btn, double cornerRadius)
    {
        var border = new Border
        {
            Width = btn.W,
            Height = btn.H,
            Background = new SolidColorBrush(Color.FromRgb(240, 240, 240)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(cornerRadius),
            Cursor = Cursors.Hand,
            Tag = btn.Code,
        };

        var textBlock = new TextBlock
        {
            Text = btn.Label,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };

        border.Child = textBlock;
        border.MouseEnter += Btn_MouseEnter;
        border.MouseLeave += Btn_MouseLeave;
        border.MouseLeftButtonDown += Btn_Click;

        Canvas.SetLeft(border, btn.X);
        Canvas.SetTop(border, btn.Y);
        canvas.Children.Add(border);
        return border;
    }

    public void UpdateColors()
    {
        if (_data.Categories.Count == 0) return;

        var modPrefix = BuildModifierPrefix();
        var allActions = _data.Categories.SelectMany(c => c.Actions).ToList();

        var activeModifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (chkLB.IsChecked == true) activeModifiers.Add("gp1_shoulderl");
        if (chkRB.IsChecked == true) activeModifiers.Add("gp1_shoulderr");

        // Clear binding list entries
        foreach (var el in canvas.Children.OfType<TextBlock>().Where(t => t.Tag is string s && s == "binding_entry").ToList())
            canvas.Children.Remove(el);

        int boundCount = 0;
        double listY = _bindingListY;

        foreach (var (code, border) in _buttonElements)
        {
            var fullCode = modPrefix + code;
            var isModifier = ModifierCodes.Contains(code);
            var isActiveModifier = isModifier && activeModifiers.Contains(code);

            List<ActionBinding> bindings;
            if (isActiveModifier)
                bindings = new List<ActionBinding>();
            else
                bindings = MouseLayoutPanel.FindBindingsForInput(allActions, fullCode, "gamepad");

            Color bg, fg, borderColor;
            if (isActiveModifier)
            {
                bg = Color.FromRgb(255, 183, 77);
                fg = Colors.White;
                borderColor = Color.FromRgb(245, 124, 0);
            }
            else if (bindings.Count > 0)
            {
                var mode = ActivationModeHelper.GetCategory(bindings[0].EffectiveGamepadActivationMode);
                (bg, fg, borderColor) = mode switch
                {
                    "hold" => (Color.FromRgb(171, 71, 188), Colors.White, Color.FromRgb(142, 36, 170)),
                    "double_tap" => (Color.FromRgb(38, 166, 154), Colors.White, Color.FromRgb(0, 137, 123)),
                    _ => (Color.FromRgb(66, 165, 245), Colors.White, Color.FromRgb(30, 136, 229)),
                };
                boundCount++;
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
                sp.Children.Add(new TextBlock { Text = InputDisplayHelper.FormatInput(fullCode), FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
                foreach (var b in bindings)
                {
                    var modeDisplay = ActivationModeHelper.GetDisplayName(b.EffectiveGamepadActivationMode);
                    var modeTag = string.IsNullOrEmpty(modeDisplay) ? "" : $"  [{modeDisplay}]";
                    sp.Children.Add(new TextBlock { Text = $"• {b.DisplayName}{modeTag}", TextWrapping = TextWrapping.Wrap });
                    sp.Children.Add(new TextBlock { Text = $"   {ActionMapNames.GetCategoryName(b.CategoryName)}", Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120)), FontSize = 11, Margin = new Thickness(0, 0, 0, 2) });
                }
                border.ToolTip = new ToolTip { Content = sp };

                foreach (var b in bindings)
                {
                    var entry = new TextBlock
                    {
                        Text = $"{InputDisplayHelper.FormatInput(code)} → {b.DisplayName}",
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Color.FromRgb(51, 51, 51)),
                        Tag = "binding_entry"
                    };
                    Canvas.SetLeft(entry, _bindingListX);
                    Canvas.SetTop(entry, listY);
                    canvas.Children.Add(entry);
                    listY += 18;
                }
            }
            else
            {
                border.ToolTip = null;
            }
        }

        txtInfo.Text = $"{boundCount} / {_buttonElements.Count} ボタンにバインド済み";
    }

    private void Btn_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border border) border.BorderThickness = new Thickness(2);
    }

    private void Btn_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border border) border.BorderThickness = new Thickness(1);
    }

    private void Btn_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.Tag is not string code) return;
        if (ModifierCodes.Contains(code)) return;

        var modPrefix = BuildModifierPrefix();
        var fullCode = modPrefix + code;
        var allActions = _data.Categories.SelectMany(c => c.Actions).ToList();
        var bindings = MouseLayoutPanel.FindBindingsForInput(allActions, fullCode, "gamepad");

        var dlg = new InputAssignDialog(_data, "gamepad", fullCode, InputDisplayHelper.FormatInput(fullCode), bindings)
        {
            Owner = Window.GetWindow(this)
        };
        if (dlg.ShowDialog() == true)
        {
            UpdateColors();
            _onBindingChanged?.Invoke();
        }
    }

    private void Modifier_Changed(object sender, RoutedEventArgs e) => UpdateColors();
}
