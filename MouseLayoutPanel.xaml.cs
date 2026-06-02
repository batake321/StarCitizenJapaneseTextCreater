using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace StarCitizenJapaneseTextCreater;

public partial class MouseLayoutPanel : UserControl
{
    private ActionMapData _data = new();
    private readonly Dictionary<string, Border> _buttonElements = new();
    private Action? _onBindingChanged;

    public MouseLayoutPanel()
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
        if (chkLShift.IsChecked == true) parts.Add("kb1_lshift");
        if (chkRShift.IsChecked == true) parts.Add("kb1_rshift");
        if (chkLCtrl.IsChecked == true) parts.Add("kb1_lctrl");
        if (chkRCtrl.IsChecked == true) parts.Add("kb1_rctrl");
        if (chkLAlt.IsChecked == true) parts.Add("kb1_lalt");
        if (chkRAlt.IsChecked == true) parts.Add("kb1_ralt");
        return parts.Count > 0 ? string.Join("+", parts) + "+" : "";
    }

    private void BuildLayout()
    {
        canvas.Children.Clear();
        _buttonElements.Clear();

        double mouseX = 80, mouseY = 30;
        double mouseW = 200, bodyH = 300;
        double btnH = 120;
        double wheelW = 40;
        double sideW = 50, sideH = 50;

        var outline = new Border
        {
            Width = mouseW,
            Height = bodyH,
            Background = new SolidColorBrush(Color.FromRgb(230, 230, 230)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(160, 160, 160)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(40, 40, 60, 60),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(outline, mouseX);
        Canvas.SetTop(outline, mouseY);
        canvas.Children.Add(outline);

        double leftBtnW = (mouseW - wheelW) / 2 - 4;
        var buttons = new List<ButtonDef>
        {
            new(mouseX + 6, mouseY + 6, leftBtnW, btnH, "左クリック\nLeft Click", "mo1_mouse1"),
            new(mouseX + mouseW - leftBtnW - 6, mouseY + 6, leftBtnW, btnH, "右クリック\nRight Click", "mo1_mouse2"),
            new(mouseX + (mouseW - wheelW) / 2, mouseY + 10, wheelW, 50, "ホイール↑\nWheel Up", "mo1_mwheel_up"),
            new(mouseX + (mouseW - wheelW) / 2, mouseY + 64, wheelW, 50, "ホイール↓\nWheel Down", "mo1_mwheel_down"),
            new(mouseX + (mouseW - 60) / 2, mouseY + 130, 60, 40, "中央\nMiddle", "mo1_mouse3"),
            new(mouseX - sideW - 8, mouseY + 60, sideW, sideH, "Mouse4\n(Side)", "mo1_mouse4"),
            new(mouseX - sideW - 8, mouseY + 60 + sideH + 8, sideW, sideH, "Mouse5\n(Side)", "mo1_mouse5"),
        };

        foreach (var btn in buttons)
        {
            var border = CreateButton(btn);
            _buttonElements[btn.Code] = border;
        }

        double axisY = mouseY + bodyH + 30;
        var axisButtons = new List<ButtonDef>
        {
            new(mouseX, axisY, 90, 50, "Mouse X\n← →", "mo1_maxis_x"),
            new(mouseX + 110, axisY, 90, 50, "Mouse Y\n↑ ↓", "mo1_maxis_y"),
        };
        foreach (var btn in axisButtons)
        {
            var border = CreateButton(btn);
            _buttonElements[btn.Code] = border;
        }

        double listX = mouseX + mouseW + 80;
        double listY = mouseY;
        var titleBlock = new TextBlock
        {
            Text = "マウスバインド一覧",
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(51, 51, 51))
        };
        Canvas.SetLeft(titleBlock, listX);
        Canvas.SetTop(titleBlock, listY);
        canvas.Children.Add(titleBlock);

        _bindingListY = listY + 26;
        _bindingListX = listX;
    }

    private double _bindingListX;
    private double _bindingListY;

    private Border CreateButton(ButtonDef btn)
    {
        var border = new Border
        {
            Width = btn.W,
            Height = btn.H,
            Background = new SolidColorBrush(Color.FromRgb(240, 240, 240)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
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
        int boundCount = 0;

        // Clear old list entries
        foreach (var el in canvas.Children.OfType<TextBlock>().Where(t => t.Tag is string s && s == "list_entry").ToList())
            canvas.Children.Remove(el);

        foreach (var (code, border) in _buttonElements)
        {
            var fullCode = modPrefix + code;
            var bindings = FindBindingsForInput(allActions, fullCode, "mouse");

            Color bg, fg, borderColor;
            if (bindings.Count > 0)
            {
                var mode = ActivationModeHelper.GetCategory(bindings[0].EffectiveMouseActivationMode);
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
                    sp.Children.Add(new TextBlock { Text = $"• {b.DisplayName}", TextWrapping = TextWrapping.Wrap });
                    sp.Children.Add(new TextBlock { Text = $"   {ActionMapNames.GetCategoryName(b.CategoryName)}", Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120)), FontSize = 11, Margin = new Thickness(0, 0, 0, 2) });
                }
                border.ToolTip = new ToolTip { Content = sp };
            }
            else
            {
                border.ToolTip = null;
            }
        }

        // Build full mouse binding list on right side
        double listY = _bindingListY;
        var mouseBindings = allActions
            .Where(a => !string.IsNullOrEmpty(a.Mouse))
            .OrderBy(a => a.Mouse)
            .ToList();

        foreach (var b in mouseBindings)
        {
            var entry = new TextBlock
            {
                Text = $"{b.MouseDisplay} → {b.DisplayName}",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(51, 51, 51)),
                Tag = "list_entry"
            };
            Canvas.SetLeft(entry, _bindingListX);
            Canvas.SetTop(entry, listY);
            canvas.Children.Add(entry);
            listY += 18;
        }

        canvas.Height = Math.Max(500, listY + 20);
        txtInfo.Text = $"{boundCount} / {_buttonElements.Count} ボタンにバインド済み | マウスバインド合計: {mouseBindings.Count}";
    }

    internal static List<ActionBinding> FindBindingsForInput(List<ActionBinding> allActions, string fullCode, string deviceType)
    {
        var target = NormalizeCodeStripPrefix(fullCode);
        return allActions.Where(a =>
        {
            var val = deviceType switch
            {
                "mouse" => a.Mouse,
                "gamepad" => a.Gamepad,
                "joystick1" => a.Joystick1,
                "joystick2" => a.Joystick2,
                _ => ""
            };
            return !string.IsNullOrEmpty(val) && NormalizeCodeStripPrefix(val) == target;
        }).ToList();
    }

    internal static string NormalizeCodeStripPrefix(string input)
    {
        var parts = input.Split('+');
        var normalized = new List<string>();
        foreach (var part in parts)
        {
            var p = part.Trim().ToLowerInvariant();
            if (p.StartsWith("gp1_")) p = p[4..];
            else if (p.StartsWith("mo1_")) p = p[4..];
            else if (p.StartsWith("js1_")) p = p[4..];
            else if (p.StartsWith("js2_")) p = p[4..];
            else if (p.StartsWith("kb1_")) p = p[4..];
            // Normalize btn_a/btn_b/btn_x/btn_y aliases
            if (p.StartsWith("btn_")) p = p[4..];
            normalized.Add(p);
        }
        normalized.Sort();
        return string.Join("+", normalized);
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

        var modPrefix = BuildModifierPrefix();
        var fullCode = modPrefix + code;
        var allActions = _data.Categories.SelectMany(c => c.Actions).ToList();
        var bindings = FindBindingsForInput(allActions, fullCode, "mouse");

        var dlg = new InputAssignDialog(_data, "mouse", fullCode, InputDisplayHelper.FormatInput(fullCode), bindings)
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
