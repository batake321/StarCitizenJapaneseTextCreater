using System.IO;
using System.Printing;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;

namespace StarCitizenJapaneseTextCreater;

public partial class KeybindEditorWindow : Window
{
    private ActionMapData _data = new();
    private List<ActionBinding> _allActions = new();
    private List<ActionBinding> _filteredActions = new();
    private readonly string _gamePath;
    private string _savePath;   // 「新規作成」で保存したら新しいフォルダに切り替わる

    // 物理コントローラー / HOTAS の押下の待ち受け (エディターを開いている間)
    private GameControllerInput? _controllerInput;

    public KeybindEditorWindow(string gamePath, string savePath)
    {
        InitializeComponent();
        _gamePath = gamePath ?? "";
        _savePath = savePath ?? "";
        Title = $"キーバインド エディタ — {Path.GetFileName(savePath)}";
        hotasPanel.DisplaySwapChanged = swapped => SaveDisplaySwapSetting(_savePath, swapped);
        LoadData();
        StartControllerInput();
        Closed += (_, _) => StopControllerInput();
    }

    private void StartControllerInput()
    {
        try
        {
            _controllerInput = new GameControllerInput();
            _controllerInput.InputPressed += ControllerInput_Pressed;
            _controllerInput.Start();
        }
        catch (Exception)
        {
            // 待ち受けを始められない環境では押下での強調は使えない (他の機能は従来どおり)
            StopControllerInput();
        }
    }

    private void StopControllerInput()
    {
        if (_controllerInput == null) return;
        _controllerInput.InputPressed -= ControllerInput_Pressed;
        _controllerInput.Dispose();
        _controllerInput = null;
    }

    // HOTAS タブ表示中はスティック、コントローラータブ表示中はゲームパッドの押下を強調する
    private void ControllerInput_Pressed(object? sender, GameControllerInputEventArgs e)
    {
        if (tabMain.SelectedIndex == 4 && e.Kind == GameControllerKind.Joystick)
        {
            if (_data.TryGetJsForDevice(e.VendorId, e.ProductId, out var js))
                hotasPanel.HighlightInput(js, e.InputCode);
            else
                hotasPanel.ShowPressMessage($"{e.DisplayName} {e.InputCode}: どの js 番号か判別できません");
        }
        else if (tabMain.SelectedIndex == 3 && e.Kind == GameControllerKind.Gamepad)
        {
            gamepadPanel.HighlightInput(e.InputCode);
        }
    }

    // ===== 「表示の入れ替え」の設定 (保存フォルダごと) =====
    //   保存フォルダの中には置かない (フォルダ内の全ファイルがゲームへコピーされるため)。
    //   作業フォルダ直下の JSON に {保存フォルダのフルパス(小文字): true} で覚える
    private static string DisplaySwapSettingsPath =>
        Path.Combine(App.Config.WorkingDirectory ?? "", "keybind_hotas_display_swap.json");

    private static string DisplaySwapKey(string savePath) =>
        Path.GetFullPath(savePath).TrimEnd('\\', '/').ToLowerInvariant();

    private static Dictionary<string, bool> LoadDisplaySwapSettings()
    {
        try
        {
            var path = DisplaySwapSettingsPath;
            if (File.Exists(path))
                return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(path))
                       ?? new Dictionary<string, bool>();
        }
        catch { }
        return new Dictionary<string, bool>();
    }

    private static bool LoadDisplaySwapSetting(string savePath)
    {
        if (string.IsNullOrEmpty(savePath)) return false;
        try
        {
            return LoadDisplaySwapSettings().TryGetValue(DisplaySwapKey(savePath), out var v) && v;
        }
        catch { return false; }
    }

    private static void SaveDisplaySwapSetting(string savePath, bool swapped)
    {
        if (string.IsNullOrEmpty(savePath)) return;
        try
        {
            var settings = LoadDisplaySwapSettings();
            var key = DisplaySwapKey(savePath);
            if (swapped) settings[key] = true;
            else settings.Remove(key);
            File.WriteAllText(DisplaySwapSettingsPath,
                System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"表示の入れ替えの設定を保存できませんでした:\n{ex.Message}", "エラー");
        }
    }

    private void LoadData()
    {
        try
        {
            _data = ActionMapParser.LoadFromGameAndSave(_gamePath, _savePath);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"キーバインドデータの読み込みに失敗しました:\n{ex.Message}\n\n{ex.StackTrace}", "エラー");
            _data = new ActionMapData();
        }

        _data.JoystickDisplaySwapped = LoadDisplaySwapSetting(_savePath);

        _allActions = _data.Categories.SelectMany(c => c.Actions).ToList();

        ChatService.SetKeybindData(_data);

        if (_allActions.Count == 0)
        {
            MessageBox.Show(
                "キーバインドデータが見つかりませんでした。\n\n" +
                "先に翻訳タブの［新しいパッチを読み取る］を実行して Data.p4k からデータを抽出してください。\n" +
                "または、ゲームパスが正しいか確認してください。",
                "データなし", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        cmbCategory.Items.Clear();
        cmbCategory.Items.Add(new CategoryItem { Name = "", DisplayName = "全カテゴリ", ActionCount = $"({_allActions.Count})" });
        foreach (var cat in _data.Categories.OrderBy(c => c.Name))
            cmbCategory.Items.Add(new CategoryItem
            {
                Name = cat.Name,
                DisplayName = cat.DisplayName,
                ActionCount = $"({cat.Actions.Count})"
            });
        cmbCategory.SelectedIndex = 0;

        lstCategories.ItemsSource = _data.Categories
            .OrderBy(c => c.Name)
            .Select(c => new CategoryItem
            {
                Name = c.Name,
                DisplayName = c.DisplayName,
                ActionCount = $"({c.Actions.Count})"
            }).ToList();

        ApplyFilter();

        Action onChanged = () =>
        {
            dgActions.Items.Refresh();
            ApplyFilter();
        };

        keyboardPanel.SetData(_data, onChanged);
        mousePanel.SetData(_data, onChanged);
        gamepadPanel.SetData(_data, onChanged);
        hotasPanel.SetData(_data, onChanged);
    }

    private void TabMain_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != tabMain) return;
        switch (tabMain.SelectedIndex)
        {
            case 1: keyboardPanel.UpdateKeyColors(); break;
            case 2: mousePanel.UpdateColors(); break;
            case 3: gamepadPanel.UpdateColors(); break;
            case 4: hotasPanel.UpdateBindingTables(); break;
        }
    }

    private void ApplyFilter()
    {
        if (dgActions == null || txtStatus == null) return;

        var search = txtSearch?.Text?.Trim() ?? "";
        var catItem = cmbCategory?.SelectedItem as CategoryItem;
        var catFilter = catItem?.Name ?? "";
        var inputFilter = (cmbInputFilter?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "全入力";

        _filteredActions = _allActions;

        if (!string.IsNullOrEmpty(catFilter))
            _filteredActions = _filteredActions.Where(a => a.CategoryName == catFilter).ToList();

        if (!string.IsNullOrEmpty(search))
            _filteredActions = _filteredActions.Where(a =>
                a.ActionName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                a.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                a.CategoryName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                a.KeyboardDisplayJa.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                a.GamepadDisplayJa.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                a.HotasDisplay.Contains(search, StringComparison.OrdinalIgnoreCase)
            ).ToList();

        _filteredActions = inputFilter switch
        {
            "キーボード" => _filteredActions.Where(a => !string.IsNullOrEmpty(a.Keyboard)).ToList(),
            "マウス" => _filteredActions.Where(a => !string.IsNullOrEmpty(a.Mouse)).ToList(),
            "ゲームパッド" => _filteredActions.Where(a => !string.IsNullOrEmpty(a.Gamepad)).ToList(),
            "HOTAS" => _filteredActions.Where(a => !string.IsNullOrEmpty(a.Joystick1) || !string.IsNullOrEmpty(a.Joystick2)).ToList(),
            "未割当のみ" => _filteredActions.Where(a => !a.HasAnyBinding).ToList(),
            "変更済みのみ" => _filteredActions.Where(a => a.IsModified).ToList(),
            _ => _filteredActions
        };

        dgActions.ItemsSource = _filteredActions;

        var modified = _allActions.Count(a => a.IsModified);
        var unbound = _allActions.Count(a => !a.HasAnyBinding);
        txtStatus.Text = $"全{_allActions.Count}件 | 表示{_filteredActions.Count}件 | 変更{modified}件 | 未割当{unbound}件";
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void Category_Changed(object sender, SelectionChangedEventArgs e) => ApplyFilter();
    private void InputFilter_Changed(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void CategoryList_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (lstCategories.SelectedItem is not CategoryItem item) return;
        for (int i = 0; i < cmbCategory.Items.Count; i++)
        {
            if (cmbCategory.Items[i] is CategoryItem ci && ci.Name == item.Name)
            {
                cmbCategory.SelectedIndex = i;
                break;
            }
        }
    }

    private void DgActions_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (dgActions.SelectedItem is not ActionBinding binding) return;

        var dlg = new BindingEditDialog(binding) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            dgActions.Items.Refresh();
            ApplyFilter();
        }
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() != true) return;

        var doc = new FlowDocument
        {
            PagePadding = new Thickness(40),
            ColumnWidth = double.MaxValue,
            FontFamily = new FontFamily("Meiryo UI"),
            FontSize = 10
        };

        doc.Blocks.Add(new Paragraph(new Run("Star Citizen キーバインド一覧"))
        {
            FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 10)
        });

        var table = new Table { CellSpacing = 0 };
        table.Columns.Add(new TableColumn { Width = new GridLength(160) });
        table.Columns.Add(new TableColumn { Width = new GridLength(110) });
        table.Columns.Add(new TableColumn { Width = new GridLength(80) });
        table.Columns.Add(new TableColumn { Width = new GridLength(110) });
        table.Columns.Add(new TableColumn { Width = new GridLength(150) });

        var headerGroup = new TableRowGroup();
        var headerRow = new TableRow { Background = Brushes.LightGray };
        headerRow.Cells.Add(new TableCell(new Paragraph(new Run("機能")) { FontWeight = FontWeights.Bold }));
        headerRow.Cells.Add(new TableCell(new Paragraph(new Run("キーボード")) { FontWeight = FontWeights.Bold }));
        headerRow.Cells.Add(new TableCell(new Paragraph(new Run("マウス")) { FontWeight = FontWeights.Bold }));
        headerRow.Cells.Add(new TableCell(new Paragraph(new Run("ゲームパッド")) { FontWeight = FontWeights.Bold }));
        headerRow.Cells.Add(new TableCell(new Paragraph(new Run("HOTAS")) { FontWeight = FontWeights.Bold }));
        headerGroup.Rows.Add(headerRow);
        table.RowGroups.Add(headerGroup);

        string? currentCat = null;
        var bodyGroup = new TableRowGroup();
        foreach (var action in _filteredActions)
        {
            if (action.CategoryName != currentCat)
            {
                currentCat = action.CategoryName;
                var catRow = new TableRow { Background = new SolidColorBrush(Color.FromRgb(230, 240, 250)) };
                var catCell = new TableCell(new Paragraph(new Run(
                    ActionMapNames.GetCategoryName(currentCat))) { FontWeight = FontWeights.Bold })
                { ColumnSpan = 5 };
                catRow.Cells.Add(catCell);
                bodyGroup.Rows.Add(catRow);
            }

            var row = new TableRow();
            row.Cells.Add(new TableCell(new Paragraph(new Run(action.DisplayName))));
            row.Cells.Add(new TableCell(new Paragraph(new Run(action.KeyboardDisplayJa))));
            row.Cells.Add(new TableCell(new Paragraph(new Run(action.MouseDisplayJa))));
            row.Cells.Add(new TableCell(new Paragraph(new Run(action.GamepadDisplayJa))));
            row.Cells.Add(new TableCell(new Paragraph(new Run(action.HotasDisplay))));
            bodyGroup.Rows.Add(row);
        }
        table.RowGroups.Add(bodyGroup);
        doc.Blocks.Add(table);

        var paginator = ((IDocumentPaginatorSource)doc).DocumentPaginator;
        paginator.PageSize = new Size(dlg.PrintableAreaWidth, dlg.PrintableAreaHeight);
        dlg.PrintDocument(paginator, "Star Citizen キーバインド");
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "XML Files|*.xml|CSV Files|*.csv",
            FileName = "keybinds_export.xml"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            if (dlg.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                using var writer = new StreamWriter(dlg.FileName, false, new UTF8Encoding(true));
                // HOTAS 列は 右・左 の順。見出しに実際の js 番号を付ける (例 HotasR(js2),HotasL(js1))
                int leftJs = _data.LeftJs, rightJs = 3 - leftJs;
                writer.WriteLine($"Category,Action,Keyboard,Mouse,Gamepad,HotasR(js{rightJs}),HotasL(js{leftJs})");
                foreach (var a in _filteredActions)
                {
                    var r = rightJs == 1 ? a.Joystick1Display : a.Joystick2Display;
                    var l = leftJs == 1 ? a.Joystick1Display : a.Joystick2Display;
                    writer.WriteLine($"{Csv(a.CategoryName)},{Csv(a.ActionName)},{Csv(a.KeyboardDisplay)},{Csv(a.MouseDisplay)},{Csv(a.GamepadDisplay)},{Csv(r)},{Csv(l)}");
                }
            }
            else
            {
                ActionMapParser.SaveUserOverrides(_data, dlg.FileName);
            }
            MessageBox.Show($"エクスポート完了: {dlg.FileName}", "完了");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"エラー: {ex.Message}", "エラー");
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "XML Files|*.xml",
            Title = "キーバインドXMLをインポート"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var imported = ActionMapParser.LoadFromFile(dlg.FileName);
            foreach (var (key, binding) in imported.AllBindings)
            {
                if (_data.AllBindings.TryGetValue(key, out var existing))
                {
                    if (!string.IsNullOrEmpty(binding.Keyboard)) existing.Keyboard = binding.Keyboard;
                    if (!string.IsNullOrEmpty(binding.Mouse)) existing.Mouse = binding.Mouse;
                    if (!string.IsNullOrEmpty(binding.Gamepad)) existing.Gamepad = binding.Gamepad;
                    if (!string.IsNullOrEmpty(binding.Joystick1)) existing.Joystick1 = binding.Joystick1;
                    if (!string.IsNullOrEmpty(binding.Joystick2)) existing.Joystick2 = binding.Joystick2;
                }
            }
            dgActions.Items.Refresh();
            ApplyFilter();
            MessageBox.Show("インポート完了", "完了");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"エラー: {ex.Message}", "エラー");
        }
    }

    private void SaveApply_Click(object sender, RoutedEventArgs e)
    {
        switch (AskSaveMode())
        {
            case "overwrite": SaveOverwrite(); break;
            case "new": SaveAsNew(); break;
        }
    }

    // 保存方法の選択: "overwrite" / "new" / null (キャンセル)
    private string? AskSaveMode()
    {
        string? result = null;
        var win = new Window
        {
            Title = "保存",
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
        };
        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock
        {
            Text = $"保存方法を選んでください。\n\n" +
                   $"上書き保存: 「{Path.GetFileName(_savePath)}」の actionmaps.xml の割り当てだけを書き換えます。\n" +
                   $"新規作成: 「{Path.GetFileName(_savePath)}」を複製した新しい保存データに書き込みます。",
            Margin = new Thickness(0, 0, 0, 12),
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var btnOverwrite = new Button { Content = "上書き保存", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        btnOverwrite.Click += (_, _) => { result = "overwrite"; win.DialogResult = true; };
        var btnNew = new Button { Content = "新規作成", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        btnNew.Click += (_, _) => { result = "new"; win.DialogResult = true; };
        var btnCancel = new Button { Content = "キャンセル", Padding = new Thickness(12, 4, 12, 4), IsCancel = true };
        buttons.Children.Add(btnOverwrite);
        buttons.Children.Add(btnNew);
        buttons.Children.Add(btnCancel);
        root.Children.Add(buttons);
        win.Content = root;
        win.ShowDialog();
        return result;
    }

    // 保存フォルダの actionmaps.xml に書く。既存ファイルがあれば rebind だけを書き換え、無ければ従来どおり新規に作る
    private string WriteActionMaps(string saveDir)
    {
        var outputPath = Path.Combine(saveDir, "Profiles", "default", "actionmaps.xml");
        if (File.Exists(outputPath))
        {
            ActionMapParser.SaveMergedOverrides(_data, outputPath, _data.JoystickInstancesSwapPending);
        }
        else
        {
            Directory.CreateDirectory(saveDir);
            ActionMapParser.SaveUserOverrides(_data, outputPath);
        }
        _data.JoystickInstancesSwapPending = false;
        return outputPath;
    }

    private void SaveOverwrite()
    {
        try
        {
            var outputPath = WriteActionMaps(_savePath);
            MessageBox.Show($"セーブに保存しました。\n{outputPath}\n\nゲームに反映するには「ゲームに反映」ボタンを使用してください。", "保存完了");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"エラー: {ex.Message}", "エラー");
        }
    }

    private void SaveAsNew()
    {
        var dlg = new InputDialog("新しい保存データの名前を入力してください:") { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var rawName = dlg.txtInput.Text ?? "";
        if (rawName.Any(char.IsWhiteSpace))   // 全角の空白も含む
        {
            MessageBox.Show("名前に空白 (全角を含む) は使えません。", "エラー");
            return;
        }
        var name = dlg.ResponseText;
        if (string.IsNullOrEmpty(name)) return;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name == "." || name == "..")
        {
            MessageBox.Show($"「{name}」はフォルダ名に使えません。", "エラー");
            return;
        }

        var parentDir = Path.GetDirectoryName(Path.GetFullPath(_savePath).TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(parentDir))
        {
            MessageBox.Show("保存先のフォルダを決められません。", "エラー");
            return;
        }
        var newPath = Path.Combine(parentDir, name);
        if (Directory.Exists(newPath) || File.Exists(newPath))
        {
            MessageBox.Show($"「{name}」は既にあります。別の名前を指定してください。", "エラー");
            return;
        }

        try
        {
            // 元の保存フォルダの全ファイルを複製してから、そこに上書き保存と同じ方式で書く
            if (Directory.Exists(_savePath))
                CopyDirectory(_savePath, newPath);
            else
                Directory.CreateDirectory(newPath);
            var outputPath = WriteActionMaps(newPath);

            var swapped = _data.JoystickDisplaySwapped;
            _savePath = newPath;
            Title = $"キーバインド エディタ — {Path.GetFileName(_savePath)}";
            SaveDisplaySwapSetting(_savePath, swapped);

            MessageBox.Show($"新しい保存データ「{name}」に保存しました。\n{outputPath}\n\nゲームに反映するには「ゲームに反映」ボタンを使用してください。", "保存完了");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"エラー: {ex.Message}", "エラー");
        }
    }

    private static void CopyDirectory(string srcDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(srcDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.GetDirectories(srcDir))
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static string Csv(string s) =>
        s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
}

public class CategoryItem
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ActionCount { get; set; } = "";
}
