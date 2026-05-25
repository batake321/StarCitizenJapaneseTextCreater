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
    private readonly BackendConfig? _verifyBackend;
    private readonly ObservableCollection<KnowledgeEntry> _allEntries = new();
    private string _filterCategory = "";

    public KnowledgeWindow(GameDataQueryService queryService, BackendConfig? verifyBackend = null)
    {
        InitializeComponent();
        _queryService = queryService;
        _verifyBackend = verifyBackend;
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
        if (dgKnowledge == null || txtStatus == null) return;
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

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var text = txtImport.Text?.Trim();
        if (string.IsNullOrEmpty(text)) { MessageBox.Show("テキストを貼り付けてください。", "取り込み"); return; }

        // Verify before importing
        if (_verifyBackend == null)
        {
            var askResult = MessageBox.Show(
                "検証エージェントが設定されていません。\n検証せずに取り込みますか？\n\n検証するにはチャットタブで検証エージェントを選択してください。",
                "検証なし", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (askResult != MessageBoxResult.Yes) return;
        }
        else
        {
            btnImport.IsEnabled = false;
            txtStatus.Text = "🔍 検証中...";
            try
            {
                var verifyResult = await ChatService.VerifyWithExternalAIAsync(
                    "以下の Star Citizen に関する情報は正しいですか？", text, _verifyBackend);

                if (!verifyResult.Contains("検証OK"))
                {
                    var result = MessageBox.Show(
                        $"検証エージェントからの指摘:\n\n{verifyResult}\n\nそれでも取り込みますか？",
                        "検証結果", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (result != MessageBoxResult.Yes)
                    {
                        txtStatus.Text = "取り込みをキャンセルしました。";
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                var result = MessageBox.Show(
                    $"検証エラー: {ex.Message}\n\n検証なしで取り込みますか？",
                    "検証エラー", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes) { txtStatus.Text = "取り込みをキャンセルしました。"; return; }
            }
            finally { btnImport.IsEnabled = true; }
        }

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

            var (_, isDup) = _queryService.AddKnowledgeSafe(line, category);
            if (!isDup) count++;
        }

        txtImport.Text = "";
        LoadEntries();
        txtStatus.Text = $"✅ {count} 件を検証済みで取り込みました";
    }
}
