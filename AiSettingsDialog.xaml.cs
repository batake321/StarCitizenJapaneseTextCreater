using System.Windows;
using System.Windows.Controls;

namespace StarCitizenJapaneseTextCreater;

public partial class AiSettingsDialog : Window
{
    private static readonly (string Id, string Display)[] ClaudeModels =
    {
        ("claude-opus-4-7", "Opus 4.7"),
        ("claude-opus-4-6", "Opus 4.6"),
        ("claude-sonnet-4-6", "Sonnet 4.6"),
        ("claude-sonnet-4-5-20250514", "Sonnet 4.5"),
        ("claude-haiku-4-5-20251001", "Haiku 4.5"),
        ("claude-3-5-haiku-20241022", "Haiku 3.5"),
    };

    private static readonly (string Id, string Display)[] GeminiModels =
    {
        ("gemini-2.5-pro", "2.5 Pro"),
        ("gemini-2.5-flash", "2.5 Flash"),
        ("gemini-2.0-flash", "2.0 Flash"),
        ("gemini-2.0-flash-lite", "2.0 Flash Lite"),
        ("gemini-1.5-pro", "1.5 Pro"),
        ("gemini-1.5-flash", "1.5 Flash"),
    };

    private static readonly (string Id, string Display)[] OpenAiModels =
    {
        ("gpt-4.1", "GPT-4.1"),
        ("gpt-4.1-mini", "GPT-4.1 Mini"),
        ("gpt-4.1-nano", "GPT-4.1 Nano"),
        ("gpt-4o", "GPT-4o"),
        ("gpt-4o-mini", "GPT-4o Mini"),
        ("o3-mini", "o3-mini"),
    };

    private readonly List<BackendPanel> _panels = new();

    public List<BackendConfig>? Result { get; private set; }

    public AiSettingsDialog(List<BackendConfig> backends)
    {
        InitializeComponent();
        foreach (var b in backends)
            AddPanel(b);
    }

    private void AddPanel(BackendConfig config)
    {
        var panel = new BackendPanel(config, this);
        _panels.Add(panel);
        pnlBackends.Children.Add(panel.Container);
    }

    private void AddBackend_Click(object sender, RoutedEventArgs e)
    {
        AddPanel(new BackendConfig
        {
            Name = "NewBackend",
            Type = "Ollama",
            BatchSize = 20,
            Enabled = false
        });
    }

    public void RemovePanel(BackendPanel panel)
    {
        _panels.Remove(panel);
        pnlBackends.Children.Remove(panel.Container);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Result = _panels.Select(p => p.ToConfig()).ToList();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    public class BackendPanel
    {
        public Border Container { get; }
        private readonly CheckBox _chkEnabled;
        private readonly TextBox _txtName;
        private readonly ComboBox _cmbType;
        private readonly TextBox _txtApiKey;
        private readonly ComboBox _cmbModel;
        private readonly TextBox _txtModelText;
        private readonly TextBox _txtBaseUrl;
        private readonly TextBox _txtBatchSize;

        // Labels we need to show/hide
        private readonly TextBlock _lblApiKey;
        private readonly TextBlock _lblBaseUrl;
        private readonly TextBlock _lblModel;

        public BackendPanel(BackendConfig config, AiSettingsDialog owner)
        {
            _chkEnabled = new CheckBox { IsChecked = config.Enabled, VerticalAlignment = VerticalAlignment.Center };
            _txtName = new TextBox { Text = config.Name, Width = 140, Margin = new Thickness(4, 0, 0, 0) };

            _cmbType = new ComboBox { Width = 100, IsEditable = true, Margin = new Thickness(4, 0, 0, 0) };
            _cmbType.Items.Add("Claude");
            _cmbType.Items.Add("Gemini");
            _cmbType.Items.Add("OpenAI");
            _cmbType.Items.Add("Ollama");
            _cmbType.Text = config.Type;
            _cmbType.SelectionChanged += (_, _) => OnTypeChanged();

            _txtApiKey = new TextBox { Text = config.ApiKey, Margin = new Thickness(4, 2, 0, 2) };

            // ComboBox for Claude/Gemini models
            _cmbModel = new ComboBox { IsEditable = true, Margin = new Thickness(4, 2, 0, 2) };
            _cmbModel.DisplayMemberPath = "Display";
            _cmbModel.SelectedValuePath = "Id";

            // Plain TextBox for Ollama model
            _txtModelText = new TextBox { Text = config.Model, Margin = new Thickness(4, 2, 0, 2) };

            _txtBaseUrl = new TextBox { Text = config.BaseUrl, Margin = new Thickness(4, 2, 0, 2) };
            _txtBatchSize = new TextBox { Text = config.BatchSize.ToString(), Width = 60, Margin = new Thickness(4, 2, 0, 2) };

            PopulateModelList(config.Type);
            _cmbModel.SelectedValue = config.Model;
            if (_cmbModel.SelectedValue == null)
                _cmbModel.Text = config.Model;

            var btnDelete = new Button { Content = "削除", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(8, 0, 0, 0) };
            btnDelete.Click += (_, _) => owner.RemovePanel(this);

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Row 0
            var row0 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            row0.Children.Add(_chkEnabled);
            row0.Children.Add(_txtName);
            row0.Children.Add(MakeLabel("  Type:"));
            row0.Children.Add(_cmbType);
            row0.Children.Add(btnDelete);
            Grid.SetRow(row0, 0); Grid.SetColumnSpan(row0, 5);
            grid.Children.Add(row0);

            // Row 1: API Key + Model
            _lblApiKey = PlaceLabel("API Key:", 1, 0, grid);
            Grid.SetRow(_txtApiKey, 1); Grid.SetColumn(_txtApiKey, 1);
            grid.Children.Add(_txtApiKey);

            _lblModel = PlaceLabel("Model:", 1, 2, grid);
            Grid.SetRow(_cmbModel, 1); Grid.SetColumn(_cmbModel, 3);
            grid.Children.Add(_cmbModel);
            Grid.SetRow(_txtModelText, 1); Grid.SetColumn(_txtModelText, 3);
            grid.Children.Add(_txtModelText);

            // Row 2: BaseUrl + BatchSize
            _lblBaseUrl = PlaceLabel("Base URL:", 2, 0, grid);
            Grid.SetRow(_txtBaseUrl, 2); Grid.SetColumn(_txtBaseUrl, 1);
            grid.Children.Add(_txtBaseUrl);

            PlaceLabel("Batch:", 2, 2, grid);
            Grid.SetRow(_txtBatchSize, 2); Grid.SetColumn(_txtBatchSize, 3);
            grid.Children.Add(_txtBatchSize);

            Container = new Border
            {
                BorderBrush = System.Windows.Media.Brushes.Gray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 8),
                Child = grid
            };

            ApplyTypeVisibility(config.Type);
        }

        private void OnTypeChanged()
        {
            var type = _cmbType.Text;
            var prevId = GetSelectedModelId();
            PopulateModelList(type);
            _cmbModel.SelectedValue = prevId;
            if (_cmbModel.SelectedValue == null)
                _cmbModel.Text = prevId;
            ApplyTypeVisibility(type);
        }

        private void ApplyTypeVisibility(string type)
        {
            var isOllama = type.Equals("Ollama", StringComparison.OrdinalIgnoreCase);
            var isApi = type.Equals("Claude", StringComparison.OrdinalIgnoreCase)
                     || type.Equals("Gemini", StringComparison.OrdinalIgnoreCase)
                     || type.Equals("OpenAI", StringComparison.OrdinalIgnoreCase);

            // API Key: show for Claude/Gemini, hide for Ollama
            _lblApiKey.Visibility = isOllama ? Visibility.Collapsed : Visibility.Visible;
            _txtApiKey.Visibility = isOllama ? Visibility.Collapsed : Visibility.Visible;

            // BaseURL: show for Ollama, hide for Claude/Gemini
            _lblBaseUrl.Visibility = isApi ? Visibility.Collapsed : Visibility.Visible;
            _txtBaseUrl.Visibility = isApi ? Visibility.Collapsed : Visibility.Visible;

            // Model: ComboBox for Claude/Gemini, TextBox for Ollama
            _cmbModel.Visibility = isApi ? Visibility.Visible : Visibility.Collapsed;
            _txtModelText.Visibility = isApi ? Visibility.Collapsed : Visibility.Visible;

            // Sync model text between the two controls
            if (isApi)
            {
                if (!string.IsNullOrEmpty(_txtModelText.Text) && _cmbModel.SelectedValue == null)
                    _cmbModel.Text = _txtModelText.Text;
            }
            else
            {
                if (_cmbModel.SelectedItem is ModelItem item)
                    _txtModelText.Text = item.Id;
                else if (!string.IsNullOrEmpty(_cmbModel.Text))
                    _txtModelText.Text = _cmbModel.Text;
            }
        }

        private void PopulateModelList(string type)
        {
            _cmbModel.Items.Clear();
            var models = type.ToLowerInvariant() switch
            {
                "claude" => ClaudeModels,
                "gemini" => GeminiModels,
                "openai" => OpenAiModels,
                _ => Array.Empty<(string, string)>()
            };
            foreach (var (id, display) in models)
                _cmbModel.Items.Add(new ModelItem(id, display));
        }

        private string GetSelectedModelId()
        {
            var type = _cmbType.Text?.ToLowerInvariant() ?? "";
            if (type is "claude" or "gemini" or "openai")
            {
                if (_cmbModel.SelectedItem is ModelItem item)
                    return item.Id;
                return _cmbModel.Text?.Trim() ?? "";
            }
            return _txtModelText.Text?.Trim() ?? "";
        }

        public BackendConfig ToConfig() => new()
        {
            Name = _txtName.Text.Trim(),
            Type = _cmbType.Text.Trim(),
            ApiKey = _txtApiKey.Text.Trim(),
            Model = GetSelectedModelId(),
            BaseUrl = _txtBaseUrl.Text.Trim(),
            BatchSize = int.TryParse(_txtBatchSize.Text, out var bs) ? bs : 20,
            Enabled = _chkEnabled.IsChecked == true
        };

        private static TextBlock MakeLabel(string text) =>
            new() { Text = text, VerticalAlignment = VerticalAlignment.Center };

        private static TextBlock PlaceLabel(string text, int row, int col, Grid grid)
        {
            var tb = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) };
            Grid.SetRow(tb, row); Grid.SetColumn(tb, col);
            grid.Children.Add(tb);
            return tb;
        }
    }
}

public class ModelItem
{
    public string Id { get; }
    public string Display { get; }

    public ModelItem(string id, string display) { Id = id; Display = display; }
    public override string ToString() => Display;
}
