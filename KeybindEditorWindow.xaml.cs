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

    public KeybindEditorWindow(string gamePath)
    {
        InitializeComponent();
        _gamePath = gamePath;
        LoadData();
    }

    private void LoadData()
    {
        _data = ActionMapParser.LoadFromGame(_gamePath);
        _allActions = _data.Categories.SelectMany(c => c.Actions).ToList();

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
    }

    private void ApplyFilter()
    {
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
                a.KeyboardDisplay.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                a.GamepadDisplay.Contains(search, StringComparison.OrdinalIgnoreCase)
            ).ToList();

        _filteredActions = inputFilter switch
        {
            "キーボード" => _filteredActions.Where(a => !string.IsNullOrEmpty(a.Keyboard)).ToList(),
            "マウス" => _filteredActions.Where(a => !string.IsNullOrEmpty(a.Mouse)).ToList(),
            "ゲームパッド" => _filteredActions.Where(a => !string.IsNullOrEmpty(a.Gamepad)).ToList(),
            "ジョイスティック" => _filteredActions.Where(a => !string.IsNullOrEmpty(a.Joystick)).ToList(),
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
        table.Columns.Add(new TableColumn { Width = new GridLength(180) });
        table.Columns.Add(new TableColumn { Width = new GridLength(120) });
        table.Columns.Add(new TableColumn { Width = new GridLength(100) });
        table.Columns.Add(new TableColumn { Width = new GridLength(120) });

        var headerGroup = new TableRowGroup();
        var headerRow = new TableRow { Background = Brushes.LightGray };
        headerRow.Cells.Add(new TableCell(new Paragraph(new Run("機能")) { FontWeight = FontWeights.Bold }));
        headerRow.Cells.Add(new TableCell(new Paragraph(new Run("キーボード")) { FontWeight = FontWeights.Bold }));
        headerRow.Cells.Add(new TableCell(new Paragraph(new Run("マウス")) { FontWeight = FontWeights.Bold }));
        headerRow.Cells.Add(new TableCell(new Paragraph(new Run("ゲームパッド")) { FontWeight = FontWeights.Bold }));
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
                { ColumnSpan = 4 };
                catRow.Cells.Add(catCell);
                bodyGroup.Rows.Add(catRow);
            }

            var row = new TableRow();
            row.Cells.Add(new TableCell(new Paragraph(new Run(action.DisplayName))));
            row.Cells.Add(new TableCell(new Paragraph(new Run(action.KeyboardDisplay))));
            row.Cells.Add(new TableCell(new Paragraph(new Run(action.MouseDisplay))));
            row.Cells.Add(new TableCell(new Paragraph(new Run(action.GamepadDisplay))));
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
                writer.WriteLine("Category,Action,Keyboard,Mouse,Gamepad,Joystick");
                foreach (var a in _filteredActions)
                {
                    writer.WriteLine($"{Csv(a.CategoryName)},{Csv(a.ActionName)},{Csv(a.KeyboardDisplay)},{Csv(a.MouseDisplay)},{Csv(a.GamepadDisplay)},{Csv(a.JoystickDisplay)}");
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
                    if (!string.IsNullOrEmpty(binding.Joystick)) existing.Joystick = binding.Joystick;
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
        var outputPath = Path.Combine(_gamePath, "user", "client", "0", "Profiles", "default", "actionmaps.xml");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            ActionMapParser.SaveUserOverrides(_data, outputPath);
            MessageBox.Show($"キーバインドを保存しました。\n{outputPath}", "保存完了");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"エラー: {ex.Message}", "エラー");
        }
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
