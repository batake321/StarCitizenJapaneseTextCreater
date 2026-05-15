using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace StarCitizenJapaneseTextCreater;

public class KnowledgeEntry : INotifyPropertyChanged
{
    private bool _isSelected;
    public int Id { get; set; }
    public string Category { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string CreatedAtStr => CreatedAt.ToString("yyyy-MM-dd HH:mm");

    public string CategoryDisplay => Category switch
    {
        "bug" => "\U0001f41b bug",
        "term" => "\U0001f4d6 用語",
        "tip" => "\U0001f4a1 tip",
        _ => Category
    };

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class KnowledgeWindow : Window
{
    private readonly GameDataQueryService _queryService;
    private readonly ObservableCollection<KnowledgeEntry> _allEntries = new();
    private string _filterCategory = "";

    public KnowledgeWindow(GameDataQueryService queryService)
    {
        InitializeComponent();
        _queryService = queryService;
        LoadEntries();
    }

    private void LoadEntries()
    {
        _allEntries.Clear();
        foreach (var (id, category, content, createdAt) in _queryService.GetAllKnowledge())
            _allEntries.Add(new KnowledgeEntry { Id = id, Category = category, Content = content, CreatedAt = createdAt });
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var filtered = string.IsNullOrEmpty(_filterCategory)
            ? _allEntries
            : new ObservableCollection<KnowledgeEntry>(_allEntries.Where(e => e.Category == _filterCategory));
        dgKnowledge.ItemsSource = filtered;
        txtStatus.Text = $"{filtered.Count} 件 (全 {_allEntries.Count} 件)";
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (cmbFilter.SelectedItem is ComboBoxItem item)
        {
            _filterCategory = item.Tag?.ToString() ?? "";
            ApplyFilter();
        }
    }

    private IEnumerable<KnowledgeEntry> SelectedEntries()
        => ((IEnumerable<KnowledgeEntry>?)dgKnowledge.ItemsSource ?? _allEntries).Where(e => e.IsSelected);

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in (IEnumerable<KnowledgeEntry>?)dgKnowledge.ItemsSource ?? _allEntries)
            entry.IsSelected = true;
    }

    private void DeselectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _allEntries)
            entry.IsSelected = false;
    }

    private void CopyToDiscord_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedEntries().OrderBy(x => x.CreatedAt).ToList();
        if (selected.Count == 0) { MessageBox.Show("項目を選択してください。", "Discord コピー"); return; }

        var sb = new StringBuilder();
        var grouped = selected.GroupBy(x => x.Category);
        foreach (var g in grouped)
        {
            var header = g.Key switch
            {
                "bug" => "\U0001f41b バグ情報",
                "term" => "\U0001f4d6 用語",
                "tip" => "\U0001f4a1 Tips",
                _ => "\U0001f4cb その他"
            };
            sb.AppendLine($"## {header}");
            foreach (var item in g)
                sb.AppendLine($"- **[{item.CreatedAt:yyyy-MM-dd}]** {item.Content}");
            sb.AppendLine();
        }

        Clipboard.SetText(sb.ToString().TrimEnd());
        txtStatus.Text = $"{selected.Count} 件をクリップボードにコピーしました (Discord 形式)";
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedEntries().ToList();
        if (selected.Count == 0) { MessageBox.Show("項目を選択してください。", "削除"); return; }
        if (MessageBox.Show($"{selected.Count} 件の記憶を削除しますか？", "確認",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        _queryService.DeleteKnowledgeByIds(selected.Select(x => x.Id));
        LoadEntries();
    }

    private static readonly Regex TagPattern = new(@"^\[(\w+)\]\s*", RegexOptions.Compiled);
    private static readonly Regex BulletPattern = new(@"^[-*・•]\s*", RegexOptions.Compiled);
    private static readonly Regex DiscordDatePattern = new(@"^\*{0,2}\[[\d\-/]+\]\*{0,2}\s*", RegexOptions.Compiled);

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var text = txtImport.Text?.Trim();
        if (string.IsNullOrEmpty(text)) { MessageBox.Show("テキストを貼り付けてください。", "取り込み"); return; }

        var defaultCategory = (cmbImportCategory.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "general";
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        int count = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line)) continue;
            if (line.StartsWith("##") || line.StartsWith("__")) continue;

            line = BulletPattern.Replace(line, "");
            line = DiscordDatePattern.Replace(line, "");

            var category = defaultCategory;
            var tagMatch = TagPattern.Match(line);
            if (tagMatch.Success)
            {
                category = tagMatch.Groups[1].Value.ToLowerInvariant();
                line = line[tagMatch.Length..];
            }

            line = line.Trim();
            if (line.Length < 2) continue;

            _queryService.AddKnowledge(line, category);
            count++;
        }

        txtImport.Text = "";
        LoadEntries();
        txtStatus.Text = $"{count} 件を取り込みました";
    }
}
