using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace StarCitizenJapaneseTextCreater;

public partial class HotasLayoutPanel : UserControl
{
    private ActionMapData _data = new();
    private Action? _onBindingChanged;

    private record JoystickGroup(string Title, List<JoystickInput> Inputs);
    private record JoystickInput(string Label, string Code);

    private static readonly List<JoystickGroup> StickGroups = new()
    {
        new("軸 (Axes)", new()
        {
            new("X Axis (Roll)", "x"),
            new("Y Axis (Pitch)", "y"),
            new("Z Axis (Twist/Yaw)", "z"),
            new("RX Axis", "rotx"),
            new("RY Axis", "roty"),
            new("RZ Axis", "rotz"),
            new("Slider 1", "slider1"),
            new("Slider 2", "slider2"),
        }),
        new("トリガー / メインボタン", new()
        {
            new("Button 1 (Trigger)", "button1"),
            new("Button 2 (Side)", "button2"),
            new("Button 3", "button3"),
            new("Button 4", "button4"),
            new("Button 5", "button5"),
            new("Button 6", "button6"),
        }),
        new("ミニスティック (Ministick)", new()
        {
            new("Button 7", "button7"),
            new("Button 8", "button8"),
            new("Button 9", "button9"),
            new("Button 10", "button10"),
            new("Button 11", "button11"),
        }),
        new("ハットスイッチ (Hat)", new()
        {
            new("Hat1 Up", "hat1_up"),
            new("Hat1 Down", "hat1_down"),
            new("Hat1 Left", "hat1_left"),
            new("Hat1 Right", "hat1_right"),
            new("Hat2 Up", "hat2_up"),
            new("Hat2 Down", "hat2_down"),
            new("Hat2 Left", "hat2_left"),
            new("Hat2 Right", "hat2_right"),
        }),
        new("追加ボタン (12-20)", new()
        {
            new("Button 12", "button12"),
            new("Button 13", "button13"),
            new("Button 14", "button14"),
            new("Button 15", "button15"),
            new("Button 16", "button16"),
            new("Button 17", "button17"),
            new("Button 18", "button18"),
            new("Button 19", "button19"),
            new("Button 20", "button20"),
        }),
        new("追加ボタン (21-32)", new()
        {
            new("Button 21", "button21"),
            new("Button 22", "button22"),
            new("Button 23", "button23"),
            new("Button 24", "button24"),
            new("Button 25", "button25"),
            new("Button 26", "button26"),
            new("Button 27", "button27"),
            new("Button 28", "button28"),
            new("Button 29", "button29"),
            new("Button 30", "button30"),
            new("Button 31", "button31"),
            new("Button 32", "button32"),
        }),
    };

    public HotasLayoutPanel()
    {
        InitializeComponent();
    }

    // 表に無い番号 (ボタン 33 以降、ハット 3 以降) を押したときに足した行 (js 番号ごと。修飾キーと js 番号を付けない部分コード)。
    //   表を作り直しても残し (UpdateBindingTables で毎回この行も作る)、新しいデータを受け取ったら (SetData) 消す
    private readonly Dictionary<int, List<string>> _extraInputs = new() { [1] = new(), [2] = new() };

    private static readonly System.Text.RegularExpressions.Regex ButtonCodeRegex = new(@"^button(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex HatCodeRegex = new(@"^hat(\d+)_(up|down|left|right)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly string[] HatDirections = { "up", "down", "left", "right" };

    public void SetData(ActionMapData data, Action? onBindingChanged)
    {
        _data = data;
        _onBindingChanged = onBindingChanged;
        _extraInputs[1].Clear();
        _extraInputs[2].Clear();
        UpdateBindingTables();
    }

    // 表に無い番号として行を足せる入力か (ボタン 33 以降、ハット 3 以降の上下左右)
    private static bool IsExtraInputCode(string code)
    {
        var b = ButtonCodeRegex.Match(code);
        if (b.Success) return int.TryParse(b.Groups[1].Value, out var n) && n >= 33;
        var h = HatCodeRegex.Match(code);
        if (h.Success) return int.TryParse(h.Groups[1].Value, out var n) && n >= 3;
        return false;
    }

    // 足した行の並び: ボタン (番号順) → ハット (番号順、上下左右の順)
    private static (int Kind, int Number, int Dir) ExtraInputSortKey(string code)
    {
        var b = ButtonCodeRegex.Match(code);
        if (b.Success && int.TryParse(b.Groups[1].Value, out var bn)) return (0, bn, 0);
        var h = HatCodeRegex.Match(code);
        if (h.Success && int.TryParse(h.Groups[1].Value, out var hn))
            return (1, hn, Array.IndexOf(HatDirections, h.Groups[2].Value.ToLowerInvariant()));
        return (2, 0, 0);
    }

    // 足した行の見出し (既存の行と同じ書き方: "Button 40"、"Hat3 Up")
    private static string ExtraInputLabel(string code)
    {
        var b = ButtonCodeRegex.Match(code);
        if (b.Success) return $"Button {b.Groups[1].Value}";
        var h = HatCodeRegex.Match(code);
        if (h.Success)
        {
            var d = h.Groups[2].Value.ToLowerInvariant();
            return $"Hat{h.Groups[1].Value} {char.ToUpperInvariant(d[0])}{d[1..]}";
        }
        return code;
    }

    private string BuildModifierPrefix()
    {
        var parts = new List<string>();
        if (chkRCtrl.IsChecked == true) parts.Add("kb1_rctrl");
        if (chkLShift.IsChecked == true) parts.Add("kb1_lshift");
        if (chkRShift.IsChecked == true) parts.Add("kb1_rshift");
        return parts.Count > 0 ? string.Join("+", parts) + "+" : "";
    }

    // 「表示の入れ替え」の設定が変わったときに呼ぶ (呼び出し元が機器の定義の指紋ごとに覚える)
    public Action<bool>? DisplaySwapChanged { get; set; }

    // ［js1/js2 の入れ替え］をしたときに呼ぶ (呼び出し元が「元の js1 の機器」の判定の表示を書き直す)
    public Action? InstanceSwapped { get; set; }

    // ［記録を消す］(元の js1 の機器の記録) を押したときに呼ぶ (確かめと記録の書き換えは呼び出し元が行う)
    public Action? ClearOriginalJs1Requested { get; set; }

    // 元の js1 の機器の記録と、今の保存データの判定を出す。canClear = ［記録を消す］を押せるか (記録があるか)
    public void ShowOriginalJs1Status(string text, bool canClear)
    {
        txtOriginalJs1.Text = text;
        btnClearOriginalJs1.IsEnabled = canClear;
    }

    private void ClearOriginalJs1_Click(object sender, RoutedEventArgs e) => ClearOriginalJs1Requested?.Invoke();

    // 物理スティックを押したときに強調している行
    private Border? _pressHighlight;

    private int LeftJs => _data.LeftJs;
    private int RightJs => 3 - _data.LeftJs;

    private static string GetJoystickValue(ActionBinding a, int js) => js == 1 ? a.Joystick1 : a.Joystick2;

    public void UpdateBindingTables()
    {
        int leftJs = LeftJs, rightJs = RightJs;
        txtHeaderLeft.Text = $"HOTAS L ({_data.GetJsHeading(leftJs)})";
        txtHeaderRight.Text = $"HOTAS R ({_data.GetJsHeading(rightJs)})";

        if (_data.Categories.Count == 0) return;

        var modPrefix = BuildModifierPrefix();
        var allActions = _data.Categories.SelectMany(c => c.Actions).ToList();

        _pressHighlight = null;
        txtPressInfo.Text = "";
        HideJsChoice();
        BuildStickPanel(panelRight, $"js{rightJs}_", $"joystick{rightJs}", allActions, modPrefix, _extraInputs[rightJs]);
        BuildStickPanel(panelLeft, $"js{leftJs}_", $"joystick{leftJs}", allActions, modPrefix, _extraInputs[leftJs]);

        // 割り当て解除 ("js1_ " など) は数えない
        int rCount = allActions.Count(a => InputCodeHelper.IsAssigned(GetJoystickValue(a, rightJs)));
        int lCount = allActions.Count(a => InputCodeHelper.IsAssigned(GetJoystickValue(a, leftJs)));
        txtInfo.Text = $"HOTAS R: {rCount} バインド | HOTAS L: {lCount} バインド";
    }

    // 物理スティックが押されたとき: js 番号を決めて行を強調する。
    //   options から js 番号を決められなければ「この機器は js◯」を選ぶ欄を出す (手で選んだ番号のときも、選び直せるように出す)
    public void HandleJoystickPress(GameControllerInputEventArgs e)
    {
        if (_data.TryResolveJsForDevice(e.VendorId, e.ProductId, e.DeviceId, out var js, out var manual))
        {
            HighlightInput(js, e.InputCode);
            if (manual)
            {
                if (txtPressInfo.Text.Length == 0)
                    txtPressInfo.Text = $"{e.DisplayName}: 手で選んだ js{js} として表示しています";
                ShowJsChoice(e);
            }
        }
        else
        {
            ShowPressMessage($"{e.DisplayName} {e.InputCode}: どの js 番号か判別できません");
            ShowJsChoice(e);
        }
    }

    // 物理スティックが押されたとき: js 番号のパネルで、今の修飾キーのチェックを加味した行を強調してスクロールする。
    //   表に無い番号 (ボタン 33 以降、ハット 3 以降) なら、その行を表の最後に足してから強調する
    public void HighlightInput(int js, string inputCode)
    {
        ClearPressHighlight();
        HideJsChoice();
        var row = FindRow(js, inputCode);
        bool added = false;
        if (row == null && (js == 1 || js == 2) && IsExtraInputCode(inputCode))
        {
            var list = _extraInputs[js];
            if (!list.Contains(inputCode, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(inputCode.ToLowerInvariant());
                list.Sort((a, b) => ExtraInputSortKey(a).CompareTo(ExtraInputSortKey(b)));
            }
            UpdateBindingTables();
            row = FindRow(js, inputCode);
            added = true;
        }
        if (row == null)
        {
            txtPressInfo.Text = $"js{js} {inputCode}: 該当する行がありません";
            return;
        }
        txtPressInfo.Text = "";
        row.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 111, 0));
        row.BorderThickness = new Thickness(2);
        _pressHighlight = row;
        if (added)
        {
            // 作り直した直後は行の配置がまだ決まっていないので、配置が済んでからスクロールする
            var target = row;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => { if (_pressHighlight == target) target.BringIntoView(); });
        }
        else
        {
            row.BringIntoView();
        }
    }

    // js 番号のパネルで、今の修飾キーのチェックを加味した入力の行。無ければ null
    private Border? FindRow(int js, string inputCode)
    {
        var panel = js == LeftJs ? panelLeft : panelRight;
        var fullCode = InputCodeHelper.ToGameForm(BuildModifierPrefix() + $"js{js}_" + inputCode, null);
        return panel.Children.OfType<Border>()
            .FirstOrDefault(b => b.Tag is HotasRowTag t && string.Equals(t.FullCode, fullCode, StringComparison.OrdinalIgnoreCase));
    }

    // 押された入力についての知らせ (js 番号が分からないときなど)
    public void ShowPressMessage(string message)
    {
        ClearPressHighlight();
        HideJsChoice();
        txtPressInfo.Text = message;
    }

    // 「この機器は js◯」を選ぶ欄に出している押下
    private GameControllerInputEventArgs? _jsChoicePress;

    private void ShowJsChoice(GameControllerInputEventArgs e)
    {
        _jsChoicePress = e;
        btnChooseJs1.Content = $"js1 (HOTAS {_data.GetJsSideLabel(1)})";
        btnChooseJs2.Content = $"js2 (HOTAS {_data.GetJsSideLabel(2)})";
        pnlJsChoice.Visibility = Visibility.Visible;
    }

    private void HideJsChoice()
    {
        _jsChoicePress = null;
        pnlJsChoice.Visibility = Visibility.Collapsed;
    }

    private void ChooseJs1_Click(object sender, RoutedEventArgs e) => ChooseJs(1);
    private void ChooseJs2_Click(object sender, RoutedEventArgs e) => ChooseJs(2);

    // 選んだ js 番号を覚えて (ActionMapData。保存フォルダごとの記録はエディターが行う)、同じ押下で行を強調し直す
    private void ChooseJs(int js)
    {
        if (_jsChoicePress is not { } press) return;
        _data.SetManualJsForDevice(press.VendorId, press.ProductId, press.DeviceId, js);
        HandleJoystickPress(press);
    }

    private void ClearPressHighlight()
    {
        if (_pressHighlight == null) return;
        _pressHighlight.BorderBrush = new SolidColorBrush(Color.FromRgb(224, 224, 224));
        _pressHighlight.BorderThickness = new Thickness(0, 0, 0, 1);
        _pressHighlight = null;
    }

    private void DisplaySwap_Click(object sender, RoutedEventArgs e)
    {
        _data.JoystickDisplaySwapped = !_data.JoystickDisplaySwapped;
        DisplaySwapChanged?.Invoke(_data.JoystickDisplaySwapped);
        UpdateBindingTables();
        _onBindingChanged?.Invoke();   // 機能別タブの HOTAS 列の R/L 表記を更新するため
    }

    private void InstanceSwap_Click(object sender, RoutedEventArgs e)
    {
        var msg = "全ての機能の js1 と js2 の割り当てを入れ替えます。\n" +
                  "保存すると actionmaps.xml の joystick の instance=\"1\" と instance=\"2\" の設定 (Product など) も入れ替わります。\n\n" +
                  "入れ替えますか？";
        if (MessageBox.Show(msg, "js1/js2 の入れ替え", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _data.SwapJoystick1And2Bindings();
        // 足した行 (表に無い番号) も、割り当てといっしょに反対側の js 番号へ移す (2 回で元に戻る)
        (_extraInputs[1], _extraInputs[2]) = (_extraInputs[2], _extraInputs[1]);
        UpdateBindingTables();
        _onBindingChanged?.Invoke();
        InstanceSwapped?.Invoke();
    }

    // extraCodes: 表に無い番号を押して足した行 (あれば最後に「表に無い入力 (押したもの)」の組として出す)
    private void BuildStickPanel(StackPanel panel, string prefix, string deviceType, List<ActionBinding> allActions, string modPrefix, IReadOnlyList<string> extraCodes)
    {
        panel.Children.Clear();

        var groups = StickGroups;
        if (extraCodes.Count > 0)
            groups = StickGroups
                .Append(new JoystickGroup("表に無い入力 (押したもの)", extraCodes.Select(c => new JoystickInput(ExtraInputLabel(c), c)).ToList()))
                .ToList();

        foreach (var group in groups)
        {
            bool hasAnyBinding = false;
            var rows = new List<UIElement>();

            foreach (var input in group.Inputs)
            {
                var fullCode = InputCodeHelper.ToGameForm(modPrefix + prefix + input.Code, null);   // ゲームの形 "js1_rctrl+button3"
                var bindings = MouseLayoutPanel.FindBindingsForInput(allActions, fullCode, deviceType);

                var row = CreateBindingRow(input.Label, fullCode, bindings, deviceType);
                rows.Add(row);
                if (bindings.Count > 0) hasAnyBinding = true;
            }

            var groupHeader = new TextBlock
            {
                Text = group.Title,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Margin = new Thickness(0, 8, 0, 4),
                Foreground = new SolidColorBrush(hasAnyBinding ? Color.FromRgb(30, 136, 229) : Color.FromRgb(100, 100, 100))
            };
            panel.Children.Add(groupHeader);

            foreach (var row in rows)
                panel.Children.Add(row);
        }
    }

    private Border CreateBindingRow(string label, string fullCode, List<ActionBinding> bindings, string deviceType)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelBlock = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Padding = new Thickness(4, 2, 4, 2)
        };
        Grid.SetColumn(labelBlock, 0);
        grid.Children.Add(labelBlock);

        var bindingText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            FontFamily = new FontFamily("Consolas"),
            Padding = new Thickness(4, 2, 4, 2),
            TextWrapping = TextWrapping.Wrap
        };

        if (bindings.Count > 0)
        {
            var parts = bindings.Select(b =>
            {
                var modeDisplay = ActivationModeHelper.GetDisplayName(
                    deviceType == "joystick1" ? b.EffectiveJoystick1ActivationMode : b.EffectiveJoystick2ActivationMode);
                var modeTag = string.IsNullOrEmpty(modeDisplay) ? "" : $" [{modeDisplay}]";
                return $"{b.DisplayName}{modeTag}";
            });
            bindingText.Text = string.Join(", ", parts);
            bindingText.Foreground = new SolidColorBrush(Color.FromRgb(30, 30, 30));
        }
        else
        {
            bindingText.Text = "—";
            bindingText.Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200));
        }

        Grid.SetColumn(bindingText, 1);
        grid.Children.Add(bindingText);

        Color bg;
        if (bindings.Count > 0)
        {
            var mode = ActivationModeHelper.GetCategory(
                deviceType == "joystick1" ? bindings[0].EffectiveJoystick1ActivationMode : bindings[0].EffectiveJoystick2ActivationMode);
            bg = mode switch
            {
                "hold" => Color.FromRgb(243, 229, 245),
                "double_tap" => Color.FromRgb(224, 242, 241),
                _ => Color.FromRgb(227, 242, 253),
            };
        }
        else
        {
            bg = Colors.White;
        }

        var border = new Border
        {
            Child = grid,
            Background = new SolidColorBrush(bg),
            BorderBrush = new SolidColorBrush(Color.FromRgb(224, 224, 224)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Cursor = Cursors.Hand,
            Tag = new HotasRowTag(deviceType, fullCode)
        };

        border.MouseEnter += Row_MouseEnter;
        border.MouseLeave += Row_MouseLeave;
        border.MouseLeftButtonDown += Row_Click;

        if (bindings.Count > 0)
        {
            var sp = new StackPanel { MaxWidth = 400 };
            sp.Children.Add(new TextBlock { Text = InputDisplayHelper.FormatInputJa(fullCode), FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
            foreach (var b in bindings)
            {
                sp.Children.Add(new TextBlock { Text = $"• {b.DisplayName}", TextWrapping = TextWrapping.Wrap });
                sp.Children.Add(new TextBlock
                {
                    Text = $"   {ActionMapNames.GetCategoryName(b.CategoryName)}",
                    Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120)),
                    FontSize = 11
                });
            }
            border.ToolTip = new ToolTip { Content = sp };
        }

        return border;
    }

    private record HotasRowTag(string DeviceType, string FullCode);

    private void Row_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border border)
            border.Background = new SolidColorBrush(Color.FromRgb(245, 245, 255));
    }

    private void Row_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not Border border || border.Tag is not HotasRowTag tag) return;
        var allActions = _data.Categories.SelectMany(c => c.Actions).ToList();
        var bindings = MouseLayoutPanel.FindBindingsForInput(allActions, tag.FullCode, tag.DeviceType);

        Color bg;
        if (bindings.Count > 0)
        {
            var mode = ActivationModeHelper.GetCategory(
                tag.DeviceType == "joystick1" ? bindings[0].EffectiveJoystick1ActivationMode : bindings[0].EffectiveJoystick2ActivationMode);
            bg = mode switch
            {
                "hold" => Color.FromRgb(243, 229, 245),
                "double_tap" => Color.FromRgb(224, 242, 241),
                _ => Color.FromRgb(227, 242, 253),
            };
        }
        else
        {
            bg = Colors.White;
        }
        border.Background = new SolidColorBrush(bg);
    }

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.Tag is not HotasRowTag tag) return;

        var allActions = _data.Categories.SelectMany(c => c.Actions).ToList();
        var bindings = MouseLayoutPanel.FindBindingsForInput(allActions, tag.FullCode, tag.DeviceType);

        var dlg = new InputAssignDialog(_data, tag.DeviceType, tag.FullCode, InputDisplayHelper.FormatInputJa(tag.FullCode), bindings)
        {
            Owner = Window.GetWindow(this)
        };
        if (dlg.ShowDialog() == true)
        {
            UpdateBindingTables();
            _onBindingChanged?.Invoke();
        }
    }

    private void ImageR_Click(object sender, MouseButtonEventArgs e) =>
        ShowImageEditor("pack://application:,,,/image/hotas/vkb_scg_R.png", $"HOTAS R ({_data.GetJsHeading(RightJs)})", $"js{RightJs}_", $"joystick{RightJs}", isRight: true);

    private void ImageL_Click(object sender, MouseButtonEventArgs e) =>
        ShowImageEditor("pack://application:,,,/image/hotas/vkb_scg_L.png", $"HOTAS L ({_data.GetJsHeading(LeftJs)})", $"js{LeftJs}_", $"joystick{LeftJs}", isRight: false);

    private void ShowImageEditor(string imageUri, string title, string jsPrefix, string deviceType, bool isRight)
    {
        var win = new Window
        {
            Title = $"{title} — バインド編集",
            Width = 1300,
            Height = 800,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this)
        };

        // === Left: Zoomable image ===
        var img = new Image
        {
            Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(imageUri, UriKind.Absolute)),
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        var scaleTransform = new ScaleTransform(0.6, 0.6);
        img.LayoutTransform = scaleTransform;

        var imageSv = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = img
        };
        imageSv.PreviewMouseWheel += (s, ev) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                var delta = ev.Delta > 0 ? 0.1 : -0.1;
                var ns = Math.Clamp(scaleTransform.ScaleX + delta, 0.2, 5.0);
                scaleTransform.ScaleX = ns;
                scaleTransform.ScaleY = ns;
                win.Title = $"{title} — バインド編集 ({(int)(ns * 100)}%)";
                ev.Handled = true;
            }
        };

        var toolbar = new WrapPanel { Margin = new Thickness(4, 4, 4, 4) };
        toolbar.Children.Add(new TextBlock { Text = "Ctrl+ホイールで拡縮", VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120)), FontSize = 11, Margin = new Thickness(0, 0, 8, 0) });
        var zoomIn = new Button { Content = "+", Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 2, 0) };
        zoomIn.Click += (_, _) => { var ns = Math.Min(scaleTransform.ScaleX + 0.2, 5.0); scaleTransform.ScaleX = ns; scaleTransform.ScaleY = ns; };
        toolbar.Children.Add(zoomIn);
        var zoomOut = new Button { Content = "−", Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 2, 0) };
        zoomOut.Click += (_, _) => { var ns = Math.Max(scaleTransform.ScaleX - 0.2, 0.2); scaleTransform.ScaleX = ns; scaleTransform.ScaleY = ns; };
        toolbar.Children.Add(zoomOut);
        var fitBtn = new Button { Content = "リセット", Padding = new Thickness(6, 1, 6, 1) };
        fitBtn.Click += (_, _) => { scaleTransform.ScaleX = 0.6; scaleTransform.ScaleY = 0.6; win.Title = $"{title} — バインド編集"; };
        toolbar.Children.Add(fitBtn);

        var leftPanel = new Grid();
        leftPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        leftPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(toolbar, 0);
        Grid.SetRow(imageSv, 1);
        leftPanel.Children.Add(toolbar);
        leftPanel.Children.Add(imageSv);

        // === Right: Binding editor table ===
        var bindingPanel = new StackPanel { Margin = new Thickness(4) };
        var allActions = _data.Categories.SelectMany(c => c.Actions).ToList();
        var modPrefix = BuildModifierPrefix();

        foreach (var group in StickGroups)
        {
            var groupHeader = new TextBlock
            {
                Text = group.Title,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Margin = new Thickness(0, 10, 0, 4),
                Foreground = new SolidColorBrush(Color.FromRgb(30, 136, 229))
            };
            bindingPanel.Children.Add(groupHeader);

            foreach (var input in group.Inputs)
            {
                var fullCode = InputCodeHelper.ToGameForm(modPrefix + jsPrefix + input.Code, null);
                var bindings = MouseLayoutPanel.FindBindingsForInput(allActions, fullCode, deviceType);

                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var labelBlock = new TextBlock
                {
                    Text = input.Label,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(4, 3, 4, 3)
                };
                Grid.SetColumn(labelBlock, 0);
                row.Children.Add(labelBlock);

                var valBlock = new TextBlock
                {
                    FontSize = 12,
                    FontFamily = new FontFamily("Consolas"),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Padding = new Thickness(4, 3, 4, 3)
                };
                if (bindings.Count > 0)
                {
                    valBlock.Text = string.Join(", ", bindings.Select(b => b.DisplayName));
                    valBlock.Foreground = new SolidColorBrush(Color.FromRgb(30, 30, 30));
                }
                else
                {
                    valBlock.Text = "—";
                    valBlock.Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200));
                }
                Grid.SetColumn(valBlock, 1);
                row.Children.Add(valBlock);

                Color bg = bindings.Count > 0 ? Color.FromRgb(227, 242, 253) : Colors.White;
                var rowBorder = new Border
                {
                    Child = row,
                    Background = new SolidColorBrush(bg),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(224, 224, 224)),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Cursor = Cursors.Hand
                };

                var capturedFullCode = fullCode;
                var capturedDeviceType = deviceType;
                rowBorder.MouseEnter += (s, _) => { if (s is Border b) b.Background = new SolidColorBrush(Color.FromRgb(245, 245, 255)); };
                rowBorder.MouseLeave += (s, _) =>
                {
                    if (s is not Border b) return;
                    var bds = MouseLayoutPanel.FindBindingsForInput(allActions, capturedFullCode, capturedDeviceType);
                    b.Background = new SolidColorBrush(bds.Count > 0 ? Color.FromRgb(227, 242, 253) : Colors.White);
                };
                rowBorder.MouseLeftButtonDown += (s, _) =>
                {
                    var bds = MouseLayoutPanel.FindBindingsForInput(allActions, capturedFullCode, capturedDeviceType);
                    var dlg = new InputAssignDialog(_data, capturedDeviceType, capturedFullCode,
                        InputDisplayHelper.FormatInputJa(capturedFullCode), bds)
                    {
                        Owner = win
                    };
                    if (dlg.ShowDialog() == true)
                    {
                        // Refresh the row
                        var newBindings = MouseLayoutPanel.FindBindingsForInput(allActions, capturedFullCode, capturedDeviceType);
                        if (newBindings.Count > 0)
                        {
                            valBlock.Text = string.Join(", ", newBindings.Select(b2 => b2.DisplayName));
                            valBlock.Foreground = new SolidColorBrush(Color.FromRgb(30, 30, 30));
                            rowBorder.Background = new SolidColorBrush(Color.FromRgb(227, 242, 253));
                        }
                        else
                        {
                            valBlock.Text = "—";
                            valBlock.Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200));
                            rowBorder.Background = new SolidColorBrush(Colors.White);
                        }
                        UpdateBindingTables();
                        _onBindingChanged?.Invoke();
                    }
                };

                bindingPanel.Children.Add(rowBorder);
            }
        }

        var bindingSv = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = bindingPanel,
            Padding = new Thickness(4)
        };

        var rightHeader = new TextBlock
        {
            Text = $"{title} — ボタン割り当て (クリックで編集)",
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            Padding = new Thickness(8, 6, 8, 6),
            // 見出し色はタブの左右の見出しと同じ (右 = オレンジ、左 = 青)
            Background = new SolidColorBrush(isRight ? Color.FromRgb(255, 243, 224) : Color.FromRgb(227, 242, 253))
        };
        var rightPanel = new Grid();
        rightPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rightPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(rightHeader, 0);
        Grid.SetRow(bindingSv, 1);
        rightPanel.Children.Add(rightHeader);
        rightPanel.Children.Add(bindingSv);

        // === Main layout: image left, bindings right ===
        var mainGrid = new Grid();
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(420) });
        Grid.SetColumn(leftPanel, 0);
        Grid.SetColumn(rightPanel, 2);
        mainGrid.Children.Add(leftPanel);

        var splitter = new GridSplitter
        {
            Width = 5,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Color.FromRgb(200, 200, 200))
        };
        Grid.SetColumn(splitter, 1);
        mainGrid.Children.Add(splitter);
        mainGrid.Children.Add(rightPanel);

        win.Content = mainGrid;
        win.Show();
    }

    private void Modifier_Changed(object sender, RoutedEventArgs e) => UpdateBindingTables();
}
