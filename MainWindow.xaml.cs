using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace StarCitizenJapaneseTextCreater;

public partial class MainWindow : Window
{
    private bool _running;
    private CancellationTokenSource? _cts;
    private DateTime _translationStartTime;
    private ChatWebServer? _webServer;

    // Editor state
    private List<TranslationRow> _allRows = new();
    private List<TranslationRow> _filteredRows = new();
    private int _page;
    private const int PageSize = 200;

    // Glossary state
    private ObservableCollection<GlossaryRow> _glossaryRows = new();

    // Trade state
    private readonly TradeService _tradeService = new();
    private HashSet<string>? _selectedCommodities;

    // Capture state
    private ScreenCaptureService? _captureService;
    private UexSubmissionService? _uexSubmitService;
    private TerminalCaptureData? _currentCapture;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var config = App.Config;

        // Restore window position/size
        RestoreWindowState(config);

        txtGamePath.Text = config.GamePath;
        txtSettingsGamePath.Text = config.GamePath;
        txtWorkDir.Text = config.WorkingDirectory;
        txtOutputLang.Text = config.OutputLanguage;
        txtScApiKey.Text = config.ScApiKey;
        txtUexApiKey.Text = config.UexApiKey;

        txtWebPort.Text = config.WebServerPort.ToString();
        txtWebHttpsPort.Text = config.WebServerHttpsPort.ToString();
        txtVoiceVoxUrl.Text = config.VoiceVoxUrl;
        txtVoiceVoxSpeaker.Text = config.VoiceVoxSpeakerId.ToString();
        chkWebAutoStart.IsChecked = config.WebServerAutoStart;
        var mfs = config.MissionDetailFontSize;
        if (mfs < 8 || mfs > 30) mfs = 14;
        txtMissionFontSize.Text = mfs.ToString();
        txtMissionDetail.FontSize = mfs;

        // Restore trade params
        txtTradeScu.Text = config.TradeScu > 0 ? config.TradeScu.ToString() : "100";
        txtTradeBudget.Text = !string.IsNullOrEmpty(config.TradeBudget) ? config.TradeBudget : "1000000";
        SelectComboByContent(cmbTradeBuySystem, config.TradeBuySystem);
        SelectComboByContent(cmbTradeSellSystem, config.TradeSellSystem);

        ChatService.OnLog += msg => Dispatcher.BeginInvoke(() =>
        {
            txtLog.AppendText(msg + "\n");
            txtLog.ScrollToEnd();
        });

        PopulateChannels();
        UpdateBackendSummary();
        UpdateDbPathDisplay();
        RefreshProfileLists();
        LoadGlossary();
        RefreshEditor();
        InitChat();
        InitCapture();
        _ = StartBackgroundTradeFetchAsync();

        if (config.WebServerAutoStart)
            _ = StartWebServerAsync();
    }

    private void PopulateChannels()
    {
        cmbChannel.SelectionChanged -= Channel_Changed;
        cmbChannel.Items.Clear();

        var channels = App.DetectGameChannels();
        foreach (var ch in channels)
            cmbChannel.Items.Add(Path.GetFileName(ch));

        if (cmbChannel.Items.Count > 0)
        {
            var currentChannel = Path.GetFileName(App.Config.GamePath);
            var idx = -1;
            for (int i = 0; i < cmbChannel.Items.Count; i++)
            {
                if (cmbChannel.Items[i]?.ToString() == currentChannel)
                { idx = i; break; }
            }
            cmbChannel.SelectedIndex = idx >= 0 ? idx : 0;
        }

        cmbChannel.SelectionChanged += Channel_Changed;
    }

    private void Channel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (cmbChannel.SelectedItem is not string channel) return;
        var gamePath = txtGamePath.Text.Trim();
        var parent = Path.GetDirectoryName(gamePath);
        if (parent != null && Directory.Exists(parent))
        {
            var newPath = Path.Combine(parent, channel);
            if (Directory.Exists(newPath))
            {
                txtGamePath.Text = newPath;
                App.Config.GamePath = newPath;
                txtSettingsGamePath.Text = newPath;
            }
        }
    }

    // === Logging ===

    private readonly StringBuilder _logBuffer = new();

    private void Log(string msg)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Log(msg));
            return;
        }
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        _logBuffer.AppendLine(line);
        txtLog.AppendText(line + "\n");
        txtLog.ScrollToEnd();
    }

    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Log Files|*.log|Text Files|*.txt",
            FileName = $"scjp_{DateTime.Now:yyyyMMdd_HHmmss}.log",
            InitialDirectory = WorkDir
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, _logBuffer.ToString(), Encoding.UTF8);
            MessageBox.Show($"ログ保存完了: {dlg.FileName}", "完了");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"エラー: {ex.Message}", "エラー");
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        txtLog.Clear();
        _logBuffer.Clear();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Log("中止を要求しました...");
        btnCancel.IsEnabled = false;
    }

    private void SetButtons(bool enabled)
    {
        Dispatcher.BeginInvoke(() =>
        {
            btnExtract.IsEnabled = enabled;
            btnTranslate.IsEnabled = enabled;
            btnApply.IsEnabled = enabled;
            btnCancel.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
            btnCancel.IsEnabled = !enabled;
        });
    }

    private void SetProgress(double pct, string detail = "")
    {
        Dispatcher.BeginInvoke(() =>
        {
            progressBar.Value = pct;
            txtProgressPct.Text = pct > 0 ? $"{pct:F1}%" : "";
            txtProgressDetail.Text = detail;
        });
    }

    private void SetTranslationProgress(int done, int total, int ok, int fail)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var pct = total > 0 ? (double)done / total * 100 : 0;
            progressBar.Value = pct;
            txtProgressPct.Text = $"{pct:F1}%";

            var detail = $"{done:N0} / {total:N0}  (成功: {ok:N0}  失敗: {fail:N0})";

            if (done > 0 && done < total)
            {
                var elapsed = DateTime.Now - _translationStartTime;
                var perItem = elapsed.TotalSeconds / done;
                var remaining = TimeSpan.FromSeconds(perItem * (total - done));
                var eta = DateTime.Now + remaining;

                string remainStr;
                if (remaining.TotalHours >= 1)
                    remainStr = $"{(int)remaining.TotalHours}時間{remaining.Minutes:D2}分";
                else if (remaining.TotalMinutes >= 1)
                    remainStr = $"{(int)remaining.TotalMinutes}分{remaining.Seconds:D2}秒";
                else
                    remainStr = $"{remaining.Seconds}秒";

                detail += $"  残り: {remainStr} (完了予測: {eta:HH:mm})";
            }

            txtProgressDetail.Text = detail;
        });
    }

    // === Path helpers ===
    private string WorkDir => App.Config.WorkingDirectory;
    private string EnPath => Path.Combine(WorkDir, "english", "global.ini");
    private string JaPath => Path.Combine(WorkDir, "japanese_(japan)", "global.ini");
    private string UntranslatedPath => Path.Combine(WorkDir, "untranslated.jsonl");
    private string TranslatedPath => Path.Combine(WorkDir, "translated.jsonl");
    private string ProgressPath => Path.Combine(WorkDir, "progress.json");
    private string OutputPath => Path.Combine(WorkDir, "output", "global.ini");
    private string DbPath => Path.Combine(WorkDir, "translations.db");
    private string CharSavesDir => Path.Combine(WorkDir, "saves", "characters");
    private string CtrlSavesDir => Path.Combine(WorkDir, "saves", "controls");

    // === AI Settings ===

    private void AiSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new AiSettingsDialog(App.Config.Translation.Backends) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result != null)
        {
            App.Config.Translation.Backends = dlg.Result;
            SaveConfigToFile();
            UpdateBackendSummary();
            RefreshChatBackends();
        }
    }

    private static bool IsChatUsable(BackendConfig b) =>
        !string.IsNullOrWhiteSpace(b.ApiKey) || b.Type == "Ollama";

    private void RefreshChatBackends()
    {
        var prevSelected = cmbChatBackend.SelectedItem as string;
        cmbChatBackend.Items.Clear();
        foreach (var b in App.Config.Translation.Backends)
        {
            if (IsChatUsable(b))
                cmbChatBackend.Items.Add($"{b.Name} ({b.Model})");
        }
        if (prevSelected != null && cmbChatBackend.Items.Contains(prevSelected))
            cmbChatBackend.SelectedItem = prevSelected;
        else if (cmbChatBackend.Items.Count > 0)
            cmbChatBackend.SelectedIndex = 0;
    }

    private void UpdateBackendSummary()
    {
        var backends = App.Config.Translation.Backends;
        if (backends.Count == 0)
        {
            txtBackendSummary.Text = "バックエンドが設定されていません";
            return;
        }

        var lines = backends.Select(b =>
        {
            var status = b.Enabled ? "有効" : "無効";
            var keyStatus = string.IsNullOrEmpty(b.ApiKey) ? "" : " (APIキー設定済)";
            return $"  {b.Name} ({b.Type}) - {status}{keyStatus} - Model: {b.Model}";
        });
        txtBackendSummary.Text = string.Join("\n", lines);
    }

    // === Translation Pipeline ===

    private void BrowseGamePath_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Star Citizen インストールディレクトリを選択" };
        if (dlg.ShowDialog() == true)
        {
            txtGamePath.Text = dlg.FolderName;
            App.Config.GamePath = dlg.FolderName;
            txtSettingsGamePath.Text = dlg.FolderName;
        }
    }

    private async void Extract_Click(object sender, RoutedEventArgs e) => await RunPipeline("extract");
    private async void Translate_Click(object sender, RoutedEventArgs e) => await RunPipeline("translate");
    private async void Apply_Click(object sender, RoutedEventArgs e) => await RunPipeline("apply");

    private async Task RunPipeline(string command)
    {
        if (_running)
        {
            if (MessageBox.Show("実行中です。中断しますか？", "確認", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                _cts?.Cancel();
            return;
        }

        _running = true;
        _cts = new CancellationTokenSource();
        SetButtons(false);
        txtLog.Clear();
        SetProgress(0);

        App.Config.GamePath = txtGamePath.Text.Trim();

        List<(string, string)>? glossary = null;
        if (File.Exists(DbPath))
        {
            try
            {
                using var gdb = new TranslationDatabase(DbPath);
                glossary = gdb.GetAllGlossary();
            }
            catch { }
        }
        TranslationBackend.SetGlossary(glossary);

        var oldOut = Console.Out;
        Console.SetOut(new UiTextWriter(Log));

        TranslationBackend.SetCacheDir(WorkDir);
        if (command == "extract")
            await TranslationBackend.FetchAndCacheProperNounsAsync();
        else if (TranslationBackend.HasCachedProperNouns())
            TranslationBackend.LoadProperNounsFromCache();
        else
            await TranslationBackend.FetchAndCacheProperNounsAsync();

        try
        {
            await Task.Run(async () =>
            {
                Dictionary<string, string>? english = null;
                Dictionary<string, string>? japanese = null;

                if (command is "extract" or "translate" or "apply")
                {
                    if (!File.Exists(EnPath) || !File.Exists(JaPath) || command == "extract")
                    {
                        Log("--- 抽出 ---");
                        SetProgress(10, "Data.p4k から global.ini を抽出中...");
                        P4kExtractor.ExtractLocalization(App.Config.GamePath, WorkDir);
                    }

                    if (File.Exists(EnPath) && File.Exists(JaPath))
                    {
                        Log("global.ini をデータベースに登録中...");
                        english = GlobalIniParser.Parse(EnPath);
                        japanese = GlobalIniParser.Parse(JaPath);
                        Log($"  English: {english.Count:N0} entries, Japanese: {japanese.Count:N0} entries");
                        using var db = new TranslationDatabase(DbPath);
                        db.ImportFromIni(english, japanese);
                    }

                    Dispatcher.Invoke(RefreshEditor);
                }

                if (command is "translate")
                {
                    Log("--- 翻訳 ---");
                    SetProgress(20, "AI 翻訳中...");

                    english ??= GlobalIniParser.Parse(EnPath);
                    japanese ??= GlobalIniParser.Parse(JaPath);

                    // 前回中断分の翻訳結果をDBに取り込む
                    if (File.Exists(TranslatedPath))
                    {
                        Log("前回の未保存翻訳をDBに取り込み中...");
                        using var importDb = new TranslationDatabase(DbPath);
                        importDb.ImportAiTranslations(TranslatedPath);
                        File.Delete(TranslatedPath);
                    }

                    // Always rebuild from DB to pick up previously failed entries
                    if (File.Exists(ProgressPath)) File.Delete(ProgressPath);
                    TranslationOrchestrator.BuildUntranslatedList(
                        english, japanese, UntranslatedPath, App.Config.ForceEnglishPatterns, DbPath);

                    var enabledBackends = App.Config.Translation.Backends
                        .Where(b => b.Enabled)
                        .Select(TranslationBackend.Create)
                        .ToList();

                    if (enabledBackends.Count == 0)
                    {
                        Log("翻訳バックエンドが有効になっていません。[AI 設定]ボタンで設定してください。");
                    }
                    else
                    {
                        _translationStartTime = DateTime.Now;
                        if (glossary?.Count > 0)
                            Log($"  用語集: {glossary.Count} 件の用語をプロンプトに含めます");

                        var progress = new ProgressTracker(ProgressPath);
                        var orchestrator = new TranslationOrchestrator(
                            enabledBackends,
                            App.Config.Translation.MaxRetries,
                            UntranslatedPath, TranslatedPath, progress);
                        orchestrator.ProgressChanged += (done, total, ok, fail) =>
                            SetTranslationProgress(done, total, ok, fail);
                        orchestrator.BatchTranslated += items => OnBatchTranslated(items);
                        try
                        {
                            await orchestrator.RunAsync(_cts!.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            Log("翻訳を中断しました。完了分をDBに保存します...");
                        }

                        if (File.Exists(TranslatedPath))
                        {
                            using var db = new TranslationDatabase(DbPath);
                            db.ImportAiTranslations(TranslatedPath);
                        }
                    }
                    SetProgress(90, "翻訳完了");
                    Dispatcher.Invoke(RefreshEditor);
                }

                if (command is "apply")
                {
                    Log("--- 反映 ---");
                    SetProgress(60, "翻訳結果を統合中...");
                    english ??= GlobalIniParser.Parse(EnPath);
                    japanese ??= GlobalIniParser.Parse(JaPath);

                    var merged = IniMerger.Merge(english, japanese, TranslatedPath,
                        App.Config.ForceEnglishPatterns, DbPath, glossary);
                    GlobalIniParser.Write(OutputPath, merged);
                    Log($"出力: {OutputPath} ({new FileInfo(OutputPath).Length:N0} bytes)");

                    SetProgress(80, "ゲームディレクトリに配置中...");
                    GameDeployer.Deploy(App.Config.GamePath, OutputPath, App.Config.OutputLanguage);
                    Log("ゲーム起動時に日本語が適用されます。");
                }

                SetProgress(100, "完了");
                Log("--- 完了 ---");
            });
        }
        catch (OperationCanceledException)
        {
            Log("中断されました。");
        }
        catch (Exception ex)
        {
            Log($"Error: {ex.Message}");
            Log(ex.StackTrace ?? "");
        }
        finally
        {
            Console.SetOut(oldOut);
            _running = false;
            _cts = null;
            btnExtract.IsEnabled = true;
            btnTranslate.IsEnabled = true;
            btnApply.IsEnabled = true;
            btnCancel.Visibility = Visibility.Collapsed;
            btnCancel.IsEnabled = false;
        }
    }

    private void OnBatchTranslated(List<(string Key, string Japanese, string Translator)> items)
    {
        foreach (var (key, ja, translator) in items)
            Log($"  翻訳: {key} → {(ja.Length > 60 ? ja[..60] + "..." : ja)}");

        Dispatcher.BeginInvoke(() =>
        {
            var rowMap = _allRows.ToDictionary(r => r.Key);
            foreach (var (key, ja, translator) in items)
            {
                if (rowMap.TryGetValue(key, out var row))
                {
                    row.Japanese = ja;
                    row.Source = "ai";
                    row.Translator = translator;
                    row.ModifiedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                }
            }
            dgTranslations.Items.Refresh();
        });
    }

    private void RefreshEditor()
    {
        if (!File.Exists(DbPath)) return;
        try
        {
            using var db = new TranslationDatabase(DbPath);
            var (total, translated, official, ai, manual, original, untranslated) = db.GetStats();
            txtDbStats.Text = $"全{total:N0}件 | 翻訳済{translated:N0} (公式{official:N0}, AI{ai:N0}, 手動{manual:N0}, 原文{original:N0}) | 未翻訳{untranslated:N0}";
            _allRows = LoadAllRows(db);
            BuildTranslatorFilter();
            ApplyFilter();
        }
        catch { }
    }

    // === Translation Editor ===

    private List<TranslationRow> LoadAllRows(TranslationDatabase db)
    {
        var rows = new List<TranslationRow>();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT key, english, japanese, source, translator, modified_at FROM translations ORDER BY key COLLATE NOCASE";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new TranslationRow
            {
                Key = reader.GetString(0),
                English = reader.GetString(1),
                Japanese = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Source = reader.GetString(3),
                Translator = reader.IsDBNull(4) ? "" : reader.GetString(4),
                ModifiedAt = reader.IsDBNull(5) ? "" : reader.GetString(5)
            });
        }
        return rows;
    }

    private void BuildTranslatorFilter()
    {
        var translators = _allRows
            .Select(r => r.Translator)
            .Where(t => !string.IsNullOrEmpty(t))
            .Distinct()
            .OrderBy(t => t)
            .ToList();

        cmbTranslatorFilter.Items.Clear();
        cmbTranslatorFilter.Items.Add("全Translator");
        foreach (var t in translators)
            cmbTranslatorFilter.Items.Add(t);
        cmbTranslatorFilter.SelectedIndex = 0;
    }

    private void ApplyFilter()
    {
        var search = txtSearch.Text.Trim();
        var sourceFilter = (cmbSourceFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "全て";
        var searchField = (cmbSearchField.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "全フィールド";
        var translatorFilter = cmbTranslatorFilter.SelectedItem?.ToString() ?? "全Translator";

        _filteredRows = _allRows;

        if (sourceFilter != "全て")
            _filteredRows = _filteredRows.Where(r => r.Source == sourceFilter).ToList();

        if (translatorFilter != "全Translator")
            _filteredRows = _filteredRows.Where(r => r.Translator == translatorFilter).ToList();

        if (!string.IsNullOrEmpty(search))
        {
            var partial = chkPartialMatch.IsChecked == true;
            var matcher = BuildSearchMatcher(search, partial);

            _filteredRows = searchField switch
            {
                "Key" => _filteredRows.Where(r => matcher(r.Key)).ToList(),
                "English" => _filteredRows.Where(r => matcher(r.English)).ToList(),
                "Japanese" => _filteredRows.Where(r => matcher(r.Japanese)).ToList(),
                _ => _filteredRows.Where(r =>
                    matcher(r.Key) || matcher(r.English) || matcher(r.Japanese)).ToList()
            };
        }

        _page = 0;
        ShowPage();
    }

    private Func<string, bool> BuildSearchMatcher(string search, bool partial)
    {
        if (search.Contains('*') || search.Contains('?'))
        {
            var escaped = Regex.Escape(search).Replace("\\*", ".*").Replace("\\?", ".");
            var pattern = partial ? escaped : "^" + escaped + "$";
            var regex = new Regex(pattern, RegexOptions.IgnoreCase);
            return value => regex.IsMatch(value);
        }

        if (partial)
            return value => value.Contains(search, StringComparison.OrdinalIgnoreCase);

        return value => value.Equals(search, StringComparison.OrdinalIgnoreCase);
    }

    private void ShowPage()
    {
        var pageData = _filteredRows.Skip(_page * PageSize).Take(PageSize).ToList();
        dgTranslations.ItemsSource = pageData;
        var totalPages = Math.Max(1, (_filteredRows.Count + PageSize - 1) / PageSize);
        txtPageInfo.Text = $"{_filteredRows.Count:N0}件 | Page {_page + 1}/{totalPages}";
    }

    private void Search_Click(object sender, RoutedEventArgs e) => ApplyFilter();
    private void SearchBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) ApplyFilter(); }
    private void SourceFilter_Changed(object sender, SelectionChangedEventArgs e) { if (_allRows.Count > 0) ApplyFilter(); }
    private void TranslatorFilter_Changed(object sender, SelectionChangedEventArgs e) { if (_allRows.Count > 0) ApplyFilter(); }
    private void SearchField_Changed(object sender, SelectionChangedEventArgs e) { if (_allRows.Count > 0) ApplyFilter(); }
    private void PartialMatch_Changed(object sender, RoutedEventArgs e) { if (_allRows.Count > 0) ApplyFilter(); }
    private void PrevPage_Click(object sender, RoutedEventArgs e) { if (_page > 0) { _page--; ShowPage(); } }
    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        if ((_page + 1) * PageSize < _filteredRows.Count) { _page++; ShowPage(); }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        var isChecked = chkSelectAll.IsChecked == true;
        var currentPage = _filteredRows.Skip(_page * PageSize).Take(PageSize);
        foreach (var row in currentPage)
            row.IsSelected = isChecked;
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = _filteredRows.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("削除する行をチェックボックスで選択してください。");
            return;
        }

        if (MessageBox.Show(
            $"選択した {selected.Count:N0} 件の翻訳(日本語)を削除します。\n削除後、再翻訳の対象になります。実行しますか？",
            "翻訳削除の確認", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            var keys = selected.Select(r => r.Key).ToList();
            using var db = new TranslationDatabase(DbPath);
            db.ClearTranslations(keys);

            foreach (var row in selected)
            {
                row.Japanese = "";
                row.Source = "untranslated";
                row.Translator = "";
                row.IsSelected = false;
                row.ModifiedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            }

            chkSelectAll.IsChecked = false;
            dgTranslations.Items.Refresh();
            MessageBox.Show($"{selected.Count:N0} 件の翻訳を削除しました。", "完了");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"エラー: {ex.Message}", "エラー");
        }
    }

    private void SetOriginal_Click(object sender, RoutedEventArgs e)
    {
        var selected = _filteredRows.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("原文にする行をチェックボックスで選択してください。");
            return;
        }

        if (MessageBox.Show(
            $"選択した {selected.Count:N0} 件の日本語を英語原文のままにします。\n実行しますか？",
            "原文設定の確認", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            var keys = selected.Select(r => r.Key).ToList();
            using var db = new TranslationDatabase(DbPath);
            db.SetToOriginalEnglish(keys);

            foreach (var row in selected)
            {
                row.Japanese = row.English;
                row.Source = "original";
                row.Translator = "original";
                row.IsSelected = false;
                row.ModifiedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            }

            chkSelectAll.IsChecked = false;
            dgTranslations.Items.Refresh();
            MessageBox.Show($"{selected.Count:N0} 件を英語原文に設定しました。", "完了");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"エラー: {ex.Message}", "エラー");
        }
    }

    private void DgTranslations_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            var row = e.Row.Item as TranslationRow;
            if (row == null) return;

            try
            {
                using var db = new TranslationDatabase(DbPath);
                db.UpdateTranslation(row.Key, row.Japanese, "manual");
                row.Source = "manual";
                row.Translator = "manual";
                row.ModifiedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存エラー: {ex.Message}", "エラー");
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void CsvExport_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(DbPath))
        {
            MessageBox.Show("データベースが見つかりません。", "エラー");
            return;
        }

        var dlg = new SaveFileDialog
        {
            Filter = "CSV Files|*.csv",
            FileName = "translations.csv",
            InitialDirectory = WorkDir
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            using var db = new TranslationDatabase(DbPath);
            db.ExportCsv(dlg.FileName);
            MessageBox.Show($"CSVエクスポート完了: {dlg.FileName}", "完了");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"エラー: {ex.Message}", "エラー");
        }
    }

    private void CsvImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "CSV Files|*.csv",
            InitialDirectory = WorkDir
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            using var db = new TranslationDatabase(DbPath);
            var count = db.ImportCsv(dlg.FileName);
            MessageBox.Show($"CSVインポート完了: {count}件", "完了");
            RefreshEditor();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"エラー: {ex.Message}", "エラー");
        }
    }

    // === Glossary ===

    private void LoadGlossary()
    {
        _glossaryRows.Clear();
        if (!File.Exists(DbPath)) return;

        try
        {
            using var db = new TranslationDatabase(DbPath);
            foreach (var (en, ja) in db.GetAllGlossary())
                _glossaryRows.Add(new GlossaryRow { English = en, Japanese = ja });
        }
        catch { }

        dgGlossary.ItemsSource = _glossaryRows;
    }

    private void GlossaryAdd_Click(object sender, RoutedEventArgs e)
    {
        var en = txtGlossEn.Text.Trim();
        var ja = txtGlossJa.Text.Trim();
        if (string.IsNullOrEmpty(en) || string.IsNullOrEmpty(ja))
        {
            MessageBox.Show("English と Japanese の両方を入力してください。", "入力エラー");
            return;
        }

        try
        {
            using var db = new TranslationDatabase(DbPath);
            db.UpsertGlossary(en, ja);
            txtGlossEn.Clear();
            txtGlossJa.Clear();
            LoadGlossary();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"エラー: {ex.Message}", "エラー");
        }
    }

    private void GlossarySelectAll_Click(object sender, RoutedEventArgs e)
    {
        var isChecked = chkGlossarySelectAll.IsChecked == true;
        foreach (var row in _glossaryRows)
            row.IsSelected = isChecked;
    }

    private void GlossaryDelete_Click(object sender, RoutedEventArgs e)
    {
        var selected = _glossaryRows.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("削除する用語をチェックボックスで選択してください。");
            return;
        }

        if (MessageBox.Show(
            $"選択した {selected.Count} 件の用語を削除します。実行しますか？",
            "用語削除の確認", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            using var db = new TranslationDatabase(DbPath);
            db.DeleteGlossaryBulk(selected.Select(r => r.English).ToList());
            chkGlossarySelectAll.IsChecked = false;
            LoadGlossary();
            MessageBox.Show($"{selected.Count} 件の用語を削除しました。", "完了");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"エラー: {ex.Message}", "エラー");
        }
    }

    private void GlossaryBulkReplace_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(DbPath))
        {
            MessageBox.Show("データベースが見つかりません。", "エラー");
            return;
        }

        if (MessageBox.Show(
            "翻訳済みテキスト内の用語集の英語を日本語に一括置換します。\nこの操作は元に戻せません。実行しますか？",
            "一括置換の確認", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            using var db = new TranslationDatabase(DbPath);
            var count = db.BulkReplaceWithGlossary();
            MessageBox.Show($"一括置換完了: {count:N0}件のエントリを更新しました。", "完了");

            RefreshEditor();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"エラー: {ex.Message}", "エラー");
        }
    }

    private void GlossaryCsvExport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "CSV Files|*.csv",
            FileName = "glossary.csv",
            InitialDirectory = WorkDir
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            using var writer = new StreamWriter(dlg.FileName, false, new UTF8Encoding(true));
            writer.WriteLine("english,japanese");
            foreach (var row in _glossaryRows)
            {
                var en = row.English.Contains(',') || row.English.Contains('"')
                    ? $"\"{row.English.Replace("\"", "\"\"")}\"" : row.English;
                var ja = row.Japanese.Contains(',') || row.Japanese.Contains('"')
                    ? $"\"{row.Japanese.Replace("\"", "\"\"")}\"" : row.Japanese;
                writer.WriteLine($"{en},{ja}");
            }
            MessageBox.Show($"用語集CSVエクスポート完了: {dlg.FileName}", "完了");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"エラー: {ex.Message}", "エラー");
        }
    }

    private void GlossaryCsvImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "CSV Files|*.csv",
            InitialDirectory = WorkDir
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            using var db = new TranslationDatabase(DbPath);
            int count = 0;
            foreach (var line in File.ReadLines(dlg.FileName, Encoding.UTF8).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var idx = line.IndexOf(',');
                if (idx <= 0) continue;
                var en = line[..idx].Trim().Trim('"');
                var ja = line[(idx + 1)..].Trim().Trim('"');
                if (en.Length > 0 && ja.Length > 0)
                {
                    db.UpsertGlossary(en, ja);
                    count++;
                }
            }
            MessageBox.Show($"用語集CSVインポート完了: {count}件", "完了");
            LoadGlossary();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"エラー: {ex.Message}", "エラー");
        }
    }

    // === Profile Management ===

    private void RefreshProfileLists()
    {
        RefreshCharSaves();
        RefreshCtrlSaves();
    }

    private void RefreshCharSaves()
    {
        lstCharSaves.Items.Clear();
        if (!Directory.Exists(CharSavesDir)) return;
        foreach (var dir in Directory.GetDirectories(CharSavesDir))
        {
            var chfCount = Directory.GetFiles(dir, "*.chf").Length;
            var time = Directory.GetLastWriteTime(dir);
            lstCharSaves.Items.Add($"{Path.GetFileName(dir)} ({chfCount} files, {time:yyyy-MM-dd HH:mm})");
        }
    }

    private void RefreshCtrlSaves()
    {
        lstCtrlSaves.Items.Clear();
        if (!Directory.Exists(CtrlSavesDir)) return;
        foreach (var dir in Directory.GetDirectories(CtrlSavesDir))
        {
            var count = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length;
            var time = Directory.GetLastWriteTime(dir);
            lstCtrlSaves.Items.Add($"{Path.GetFileName(dir)} ({count} files, {time:yyyy-MM-dd HH:mm})");
        }
    }

    private void RefreshCharSaves_Click(object sender, RoutedEventArgs e) => RefreshCharSaves();
    private void RefreshCtrlSaves_Click(object sender, RoutedEventArgs e) => RefreshCtrlSaves();

    private void SaveChar_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptInput("保存名を入力してください:");
        if (string.IsNullOrEmpty(name)) return;

        try
        {
            var savePath = Path.Combine(CharSavesDir, name);
            ProfileManager.SaveCharacter(App.Config.GamePath, savePath);
            txtProfileLog.AppendText($"[{DateTime.Now:HH:mm:ss}] キャラクター保存完了: {name}\n");
            RefreshCharSaves();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"エラー: {ex.Message}", "エラー");
        }
    }

    private void ImportChar_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "キャラクターデータのフォルダを選択 (.chf ファイルを含むフォルダ)" };
        if (dlg.ShowDialog() != true) return;

        var name = PromptInput("保存名を入力してください:");
        if (string.IsNullOrEmpty(name)) return;

        try
        {
            var destPath = Path.Combine(CharSavesDir, name);
            Directory.CreateDirectory(destPath);
            int count = 0;
            foreach (var file in Directory.GetFiles(dlg.FolderName, "*.chf"))
            {
                File.Copy(file, Path.Combine(destPath, Path.GetFileName(file)), overwrite: true);
                count++;
            }
            txtProfileLog.AppendText($"[{DateTime.Now:HH:mm:ss}] 外部取込完了: {name} ({count} files)\n");
            RefreshCharSaves();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"エラー: {ex.Message}", "エラー");
        }
    }

    private void CopyChar_Click(object sender, RoutedEventArgs e)
    {
        var selected = lstCharSaves.SelectedItem?.ToString();
        if (selected == null) { MessageBox.Show("コピー元を選択してください。"); return; }
        var srcName = selected.Split(' ')[0];
        var newName = PromptInput($"「{srcName}」のコピー先の名前を入力してください:");
        if (string.IsNullOrEmpty(newName)) return;

        try
        {
            var srcPath = Path.Combine(CharSavesDir, srcName);
            var destPath = Path.Combine(CharSavesDir, newName);
            CopyDirectory(srcPath, destPath);
            txtProfileLog.AppendText($"[{DateTime.Now:HH:mm:ss}] コピー完了: {srcName} → {newName}\n");
            RefreshCharSaves();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"エラー: {ex.Message}", "エラー");
        }
    }

    private void ApplyChar_Click(object sender, RoutedEventArgs e)
    {
        var selected = lstCharSaves.SelectedItem?.ToString();
        if (selected == null) { MessageBox.Show("反映するデータを選択してください。"); return; }
        var name = selected.Split(' ')[0];

        if (MessageBox.Show($"「{name}」をゲームに反映しますか？\n現在のキャラクターデータが上書きされます。",
            "確認", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;

        try
        {
            var loadPath = Path.Combine(CharSavesDir, name);
            ProfileManager.LoadCharacter(App.Config.GamePath, loadPath);
            txtProfileLog.AppendText($"[{DateTime.Now:HH:mm:ss}] ゲームに反映完了: {name}\n");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"エラー: {ex.Message}", "エラー");
        }
    }

    private void DeleteChar_Click(object sender, RoutedEventArgs e)
    {
        var selected = lstCharSaves.SelectedItem?.ToString();
        if (selected == null) { MessageBox.Show("削除するデータを選択してください。"); return; }
        var name = selected.Split(' ')[0];

        if (MessageBox.Show($"「{name}」を削除しますか？", "確認", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            Directory.Delete(Path.Combine(CharSavesDir, name), true);
            txtProfileLog.AppendText($"[{DateTime.Now:HH:mm:ss}] 削除完了: {name}\n");
            RefreshCharSaves();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"エラー: {ex.Message}", "エラー");
        }
    }

    private void SaveCtrl_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptInput("保存名を入力してください:");
        if (string.IsNullOrEmpty(name)) return;

        try
        {
            var savePath = Path.Combine(CtrlSavesDir, name);
            ProfileManager.SaveControls(App.Config.GamePath, savePath);
            txtProfileLog.AppendText($"[{DateTime.Now:HH:mm:ss}] コントロール設定保存完了: {name}\n");
            RefreshCtrlSaves();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"エラー: {ex.Message}", "エラー");
        }
    }

    private void ImportCtrl_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "コントロール設定のフォルダを選択" };
        if (dlg.ShowDialog() != true) return;

        var name = PromptInput("保存名を入力してください:");
        if (string.IsNullOrEmpty(name)) return;

        try
        {
            var destPath = Path.Combine(CtrlSavesDir, name);
            CopyDirectory(dlg.FolderName, destPath);
            var count = Directory.GetFiles(destPath, "*", SearchOption.AllDirectories).Length;
            txtProfileLog.AppendText($"[{DateTime.Now:HH:mm:ss}] 外部取込完了: {name} ({count} files)\n");
            RefreshCtrlSaves();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"エラー: {ex.Message}", "エラー");
        }
    }

    private void CopyCtrl_Click(object sender, RoutedEventArgs e)
    {
        var selected = lstCtrlSaves.SelectedItem?.ToString();
        if (selected == null) { MessageBox.Show("コピー元を選択してください。"); return; }
        var srcName = selected.Split(' ')[0];
        var newName = PromptInput($"「{srcName}」のコピー先の名前を入力してください:");
        if (string.IsNullOrEmpty(newName)) return;

        try
        {
            var srcPath = Path.Combine(CtrlSavesDir, srcName);
            var destPath = Path.Combine(CtrlSavesDir, newName);
            CopyDirectory(srcPath, destPath);
            txtProfileLog.AppendText($"[{DateTime.Now:HH:mm:ss}] コピー完了: {srcName} → {newName}\n");
            RefreshCtrlSaves();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"エラー: {ex.Message}", "エラー");
        }
    }

    private void ApplyCtrl_Click(object sender, RoutedEventArgs e)
    {
        var selected = lstCtrlSaves.SelectedItem?.ToString();
        if (selected == null) { MessageBox.Show("反映するデータを選択してください。"); return; }
        var name = selected.Split(' ')[0];

        if (MessageBox.Show($"「{name}」をゲームに反映しますか？\n現在のコントロール設定が上書きされます。",
            "確認", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;

        try
        {
            var loadPath = Path.Combine(CtrlSavesDir, name);
            ProfileManager.LoadControls(App.Config.GamePath, loadPath);
            txtProfileLog.AppendText($"[{DateTime.Now:HH:mm:ss}] ゲームに反映完了: {name}\n");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"エラー: {ex.Message}", "エラー");
        }
    }

    private void DeleteCtrl_Click(object sender, RoutedEventArgs e)
    {
        var selected = lstCtrlSaves.SelectedItem?.ToString();
        if (selected == null) { MessageBox.Show("削除するデータを選択してください。"); return; }
        var name = selected.Split(' ')[0];

        if (MessageBox.Show($"「{name}」を削除しますか？", "確認", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            Directory.Delete(Path.Combine(CtrlSavesDir, name), true);
            txtProfileLog.AppendText($"[{DateTime.Now:HH:mm:ss}] 削除完了: {name}\n");
            RefreshCtrlSaves();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"エラー: {ex.Message}", "エラー");
        }
    }

    private void KeybindEditor_Click(object sender, RoutedEventArgs e)
    {
        var selected = lstCtrlSaves.SelectedItem?.ToString();
        if (selected == null)
        {
            MessageBox.Show("編集するコントロール設定をリストから選択してください。\n\nまだ保存がない場合は「ゲームから保存」で現在の設定を保存してください。", "選択してください");
            return;
        }
        var name = selected.Split(' ')[0];
        var savePath = Path.Combine(CtrlSavesDir, name);

        try
        {
            var dlg = new KeybindEditorWindow(App.Config.GamePath, savePath) { Owner = this };
            dlg.ShowDialog();
            RefreshCtrlSaves();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"エラー: {ex.Message}\n\n{ex.StackTrace}", "エラー");
        }
    }

    private static void CopyDirectory(string srcDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(srcDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(srcDir))
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
    }

    // === Settings ===

    private void UpdateDbPathDisplay()
    {
        txtDbPath.Text = $"保存場所: {DbPath}";
        if (File.Exists(DbPath))
        {
            var size = new FileInfo(DbPath).Length;
            txtDbPath.Text += $" ({size / 1024.0:N0} KB)";
        }
    }

    private string IndexDbPath => _gameDataExtractor?.DbPath ?? Path.Combine(WorkDir, "gamedata_cache.db");

    private async void ExportDb_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "データベースのエクスポート先",
            Filter = "ZIP バックアップ (*.zip)|*.zip",
            FileName = $"sc_japanese_backup_{DateTime.Now:yyyyMMdd_HHmmss}.zip"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            txtBackupStatus.Text = "エクスポート中...";
            var tradeDbPath = Path.Combine(WorkDir, "trade_cache.db");
            var includeMyShips = chkExportMyShips.IsChecked == true;
            await DatabaseBackupService.ExportAsync(DbPath, IndexDbPath, dlg.FileName,
                s => Dispatcher.Invoke(() => txtBackupStatus.Text = s), tradeDbPath, includeMyShips);
            var size = new FileInfo(dlg.FileName).Length;
            txtBackupStatus.Text = $"エクスポート完了 ({size / 1024.0:N0} KB)";
            MessageBox.Show($"バックアップを保存しました。\n{dlg.FileName}\n({size / 1024.0:N0} KB)", "エクスポート完了");
        }
        catch (Exception ex)
        {
            txtBackupStatus.Text = "";
            MessageBox.Show($"エクスポートに失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ImportDb_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "バックアップファイルの選択",
            Filter = "ZIP バックアップ (*.zip)|*.zip"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            txtBackupStatus.Text = "ファイルを解析中...";
            var contents = await DatabaseBackupService.InspectZipAsync(dlg.FileName);

            if (contents.Count == 0)
            {
                txtBackupStatus.Text = "";
                MessageBox.Show("バックアップファイルにデータが見つかりませんでした。", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var selectDlg = new ImportSelectionDialog { Owner = this };
            selectDlg.SetFileInfo(Path.GetFileName(dlg.FileName), new FileInfo(dlg.FileName).Length, contents);
            if (selectDlg.ShowDialog() != true)
            {
                txtBackupStatus.Text = "";
                return;
            }

            txtBackupStatus.Text = "インポート中...";
            var tradeDbImportPath = Path.Combine(WorkDir, "trade_cache.db");
            await DatabaseBackupService.ImportFromZipAsync(dlg.FileName, DbPath, IndexDbPath,
                selectDlg.SelectedCategories, selectDlg.Mode,
                s => Dispatcher.Invoke(() => txtBackupStatus.Text = s), tradeDbImportPath);

            txtBackupStatus.Text = "インポート完了";
            UpdateDbPathDisplay();

            if (selectDlg.SelectedCategories.Contains(BackupCategory.Translations))
                RefreshEditor();
            if (selectDlg.SelectedCategories.Contains(BackupCategory.Glossary))
                LoadGlossary();

            MessageBox.Show("インポートが完了しました。", "インポート完了");
        }
        catch (Exception ex)
        {
            txtBackupStatus.Text = "";
            MessageBox.Show($"インポートに失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearDb_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(DbPath))
        {
            MessageBox.Show("データベースが存在しません。", "情報");
            return;
        }

        if (MessageBox.Show(
            "翻訳データベースを完全に削除します。\n全ての翻訳データ・用語集が失われます。\n\n本当に削除しますか？",
            "DBクリアの確認", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            File.Delete(DbPath);
            if (File.Exists(UntranslatedPath)) File.Delete(UntranslatedPath);
            if (File.Exists(TranslatedPath)) File.Delete(TranslatedPath);
            if (File.Exists(ProgressPath)) File.Delete(ProgressPath);

            _allRows.Clear();
            _filteredRows.Clear();
            _glossaryRows.Clear();
            dgTranslations.ItemsSource = null;
            dgGlossary.ItemsSource = null;
            txtDbStats.Text = "";
            txtPageInfo.Text = "";

            UpdateDbPathDisplay();
            MessageBox.Show("データベースを削除しました。", "完了");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"エラー: {ex.Message}", "エラー");
        }
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        App.Config.GamePath = txtSettingsGamePath.Text.Trim();
        App.Config.WorkingDirectory = txtWorkDir.Text.Trim();
        App.Config.OutputLanguage = txtOutputLang.Text.Trim();
        App.Config.ScApiKey = txtScApiKey.Text.Trim();
        if (int.TryParse(txtWebPort.Text.Trim(), out var port)) App.Config.WebServerPort = port;
        if (int.TryParse(txtWebHttpsPort.Text.Trim(), out var hp)) App.Config.WebServerHttpsPort = hp;
        App.Config.VoiceVoxUrl = txtVoiceVoxUrl.Text.Trim();
        if (int.TryParse(txtVoiceVoxSpeaker.Text.Trim(), out var spk)) App.Config.VoiceVoxSpeakerId = spk;
        App.Config.WebServerAutoStart = chkWebAutoStart.IsChecked == true;
        App.Config.UexApiKey = txtUexApiKey.Text.Trim();
        if (double.TryParse(txtMissionFontSize.Text.Trim(), out var fs) && fs >= 8 && fs <= 30)
        {
            App.Config.MissionDetailFontSize = fs;
            txtMissionDetail.FontSize = fs;
        }
        txtGamePath.Text = App.Config.GamePath;

        SaveConfigToFile();
        UpdateDbPathDisplay();
        MessageBox.Show("設定を保存しました。", "完了");
    }

    private void SaveConfigToFile()
    {
        try
        {
            var config = new
            {
                App.Config.GamePath,
                App.Config.WorkingDirectory,
                App.Config.OutputLanguage,
                Translation = new
                {
                    App.Config.Translation.MaxRetries,
                    App.Config.Translation.Backends
                },
                App.Config.ForceEnglishPatterns,
                App.Config.ScApiKey,
                App.Config.LastChatBackend,
                App.Config.WebServerPort,
                App.Config.WebServerHttpsPort,
                App.Config.WebServerAutoStart,
                App.Config.VoiceVoxUrl,
                App.Config.VoiceVoxSpeakerId,
                App.Config.WindowLeft,
                App.Config.WindowTop,
                App.Config.WindowWidth,
                App.Config.WindowHeight,
                App.Config.WindowMaximized,
                App.Config.TradeShipName,
                App.Config.TradeScu,
                App.Config.TradeBudget,
                App.Config.TradeBuySystem,
                App.Config.TradeSellSystem,
                App.Config.UexApiKey,
                App.Config.MissionDetailFontSize,
            };

            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            File.WriteAllText(App.ConfigPath, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"設定保存エラー: {ex.Message}", "エラー");
        }
    }

    // === Helpers ===

    private static string? PromptInput(string message)
    {
        var dlg = new InputDialog(message);
        return dlg.ShowDialog() == true ? dlg.ResponseText : null;
    }

    // === Chat Tab ===

    private readonly ObservableCollection<ChatBubble> _chatBubbles = new();
    private readonly List<ChatMessage> _chatHistory = new();
    private bool _chatSending;
    private GameDataExtractor? _gameDataExtractor;

    // ── Mission tab ──
    private MissionService? _missionService;
    private List<MissionService.MissionEntry>? _currentMissions;

    private void LoadMissions_Click(object sender, RoutedEventArgs e)
    {
        var dbPath = IndexDbPath;
        if (!File.Exists(dbPath))
        {
            MessageBox.Show("ゲームデータのインデックスが未構築です。\n設定タブの「インデックス構築」を実行してください。",
                "ミッション", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            _missionService?.Dispose();
            _missionService = new MissionService(dbPath, DbPath);
            var categories = _missionService.GetCategories();
            lstMissionCategories.ItemsSource = categories;
            var transInfo = _missionService.TransLoadError != null
                ? $"翻訳DBエラー:{_missionService.TransLoadError}"
                : $"翻訳DB:{_missionService.TransDictCount}";
            txtMissionStatus.Text = $"{categories.Sum(c => c.Count)} 件 ({transInfo})";
            dgMissions.ItemsSource = null;
            txtMissionDetail.Text = "カテゴリを選択してください。";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"ミッション読み込みエラー:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void MissionCategory_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_missionService == null) return;
        if (lstMissionCategories.SelectedItem is not MissionService.MissionCategory cat) return;

        try
        {
            txtMissionSearch.Text = "";
            txtMissionSearchStatus.Text = "";
            _currentMissions = _missionService.GetMissions(cat.Name);
            dgMissions.ItemsSource = _currentMissions;
            var jaCount = _currentMissions.Count(m => !string.IsNullOrEmpty(m.DisplayNameJa));
            txtMissionStatus.Text = $"{cat.Name}: {_currentMissions.Count} 件 (日本語:{jaCount})";
            txtMissionDetail.Text = "ミッションを選択すると詳細が表示されます。";
        }
        catch (Exception ex)
        {
            txtMissionStatus.Text = $"エラー: {ex.Message}";
        }
    }

    private void MissionSearch_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) MissionSearch_Execute();
    }
    private void MissionSearch_Click(object sender, RoutedEventArgs e) => MissionSearch_Execute();
    private void MissionSearchClear_Click(object sender, RoutedEventArgs e)
    {
        txtMissionSearch.Text = "";
        txtMissionSearchStatus.Text = "";
        if (lstMissionCategories.SelectedItem is MissionService.MissionCategory cat)
        {
            _currentMissions = _missionService?.GetMissions(cat.Name);
            dgMissions.ItemsSource = _currentMissions;
        }
        else
        {
            dgMissions.ItemsSource = null;
        }
    }

    private void MissionSearch_Execute()
    {
        if (_missionService == null)
        {
            txtMissionSearchStatus.Text = "先にミッション読み込みを実行してください";
            return;
        }
        var query = txtMissionSearch.Text.Trim();
        if (string.IsNullOrEmpty(query)) { MissionSearchClear_Click(this, new RoutedEventArgs()); return; }

        try
        {
            lstMissionCategories.SelectedIndex = -1;
            _currentMissions = _missionService.Search(query);
            dgMissions.ItemsSource = _currentMissions;
            txtMissionSearchStatus.Text = $"{_currentMissions.Count} 件";

            if (_currentMissions.Count == 0)
            {
                var transHits = _missionService.SearchTranslations(query);
                if (transHits.Count > 0)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("ミッションデータに直接の一致はありませんが、翻訳データベースに以下のタイトルが見つかりました。");
                    sb.AppendLine("（ゲーム内でランタイム生成されるミッションの可能性があります）\n");
                    foreach (var (key, en, ja) in transHits.Take(20))
                    {
                        var display = !string.IsNullOrEmpty(ja) ? $"{ja} ({en})" : en;
                        sb.AppendLine($"  ● {display}");
                        sb.AppendLine($"    キー: {key}");
                    }
                    if (transHits.Count > 20)
                        sb.AppendLine($"\n  ...他 {transHits.Count - 20} 件");
                    txtMissionDetail.Text = sb.ToString();
                    txtMissionSearchStatus.Text = $"0 件 (翻訳DB: {transHits.Count} 件)";
                }
                else
                    txtMissionDetail.Text = "該当するミッションが見つかりませんでした。";
            }
            else
                txtMissionDetail.Text = "ミッションを選択すると詳細が表示されます。";
        }
        catch (Exception ex)
        {
            txtMissionSearchStatus.Text = $"エラー: {ex.Message}";
        }
    }

    private void Mission_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (_missionService == null) return;
        if (dgMissions.SelectedItem is not MissionService.MissionEntry mission) return;

        try
        {
            txtMissionDetail.Text = _missionService.FormatDetail(mission);
        }
        catch (Exception ex)
        {
            txtMissionDetail.Text = $"詳細表示エラー: {ex.Message}";
        }
    }

    private void InitChat()
    {
        icChatMessages.ItemsSource = _chatBubbles;

        cmbChatBackend.Items.Clear();
        foreach (var b in App.Config.Translation.Backends)
        {
            if (IsChatUsable(b))
                cmbChatBackend.Items.Add($"{b.Name} ({b.Model})");
        }
        if (cmbChatBackend.Items.Count > 0)
        {
            var last = App.Config.LastChatBackend;
            var idx = string.IsNullOrEmpty(last) ? -1 : cmbChatBackend.Items.IndexOf(last);
            cmbChatBackend.SelectedIndex = idx >= 0 ? idx : 0;
        }

        var welcomeLines = new List<string> { "Star Citizen について質問してください。\nUEX API・SC Trade Tools・Wiki・ゲームファイルから最新データを取得して回答します。" };

        if (!App.Config.Translation.Backends.Any(b => IsChatUsable(b)))
            welcomeLines.Add("\n⚠️ AI バックエンドが未設定です。設定タブの「AI 設定を開く」から Claude / Gemini / Ollama を設定してください。");

        InitGameDataExtractor();

        if (_gameDataExtractor != null && !_gameDataExtractor.HasStructuredData())
            welcomeLines.Add("\n💡 ミッション・契約の検索には、設定タブの「インデックス構築」の実行が必要です（約2分半）。");

        _chatBubbles.Add(new ChatBubble { Text = string.Join("", welcomeLines), IsUser = false });
    }

    private void InitGameDataExtractor()
    {
        var workDir = App.Config.WorkingDirectory;
        if (string.IsNullOrEmpty(workDir)) workDir = AppDomain.CurrentDomain.BaseDirectory;

        _gameDataExtractor = new GameDataExtractor(workDir);
        ChatService.SetGameDataExtractor(_gameDataExtractor);
        ChatService.LogDirectory = workDir;
        ChatService.SetTranslationDbPath(DbPath);

        try
        {
            var queryService = new GameDataQueryService(_gameDataExtractor.DbPath);
            ChatService.SetGameDataQueryService(queryService);
        }
        catch { }

        // Load keybind data for chat tool
        try
        {
            var gamePath = App.Config.GamePath;
            if (!string.IsNullOrEmpty(gamePath))
            {
                var kbData = ActionMapParser.LoadFromGameAndSave(gamePath, "");
                if (kbData.Categories.Count > 0)
                    ChatService.SetKeybindData(kbData);
            }
        }
        catch { }

        var ver = _gameDataExtractor.GetCachedVersion();
        if (ver != null)
        {
            var updateNote = _gameDataExtractor.IsP4kUpdated() ? " ⚠パッチ更新あり" : "";
            txtGameDataStatus.Text = $"インデックス済み ({ver}){updateNote}";
        }
        else if (_gameDataExtractor.IsStarBreakerInstalled)
            txtGameDataStatus.Text = "未インデックス (インデックス構築で高速化)";
        else
            txtGameDataStatus.Text = "StarBreaker 未導入 (初回は自動ダウンロード)";
    }

    private async void ExtractGameData_Click(object sender, RoutedEventArgs e)
    {
        if (_gameDataExtractor == null)
        {
            MessageBox.Show("GameDataExtractor が初期化されていません。", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var p4kPath = _gameDataExtractor.FindDataP4k();
        if (string.IsNullOrEmpty(p4kPath))
        {
            var gamePath = txtSettingsGamePath.Text.Trim();
            if (!string.IsNullOrEmpty(gamePath))
            {
                var candidates = new[]
                {
                    Path.Combine(gamePath, "Data.p4k"),
                    Path.Combine(Path.GetDirectoryName(gamePath) ?? "", "Data.p4k"),
                    gamePath,
                };
                p4kPath = candidates.FirstOrDefault(File.Exists);
            }
        }

        if (string.IsNullOrEmpty(p4kPath) || !File.Exists(p4kPath))
        {
            MessageBox.Show("Data.p4k が見つかりません。\n設定タブで GamePath を正しく設定してください。",
                "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        btnExtractGameData.IsEnabled = false;
        txtGameDataStatus.Text = "構築中...";

        _gameDataExtractor.ProgressChanged += OnGameDataProgress;
        _gameDataExtractor.StatusChanged += OnGameDataStatus;

        try
        {
            await _gameDataExtractor.BuildIndexAsync(p4kPath);
            OnGameDataStatus("日本語名をマッピング中...");
            var transDbPath = Path.Combine(WorkDir, "translations.db");
            if (File.Exists(transDbPath))
                _gameDataExtractor.PopulateJapaneseNames(transDbPath);
            OnGameDataStatus("固有名詞キャッシュ取得中...");
            TranslationBackend.SetCacheDir(WorkDir);
            await TranslationBackend.FetchAndCacheProperNounsAsync();
            OnGameDataStatus("固有名詞キャッシュ完了");
            var ver = _gameDataExtractor.GetCachedVersion();
            txtGameDataStatus.Text = $"インデックス済み ({ver})";
            UpdateSearchIndexStatus();
        }
        catch (Exception ex)
        {
            txtGameDataStatus.Text = "構築失敗";
            MessageBox.Show($"インデックス構築エラー:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _gameDataExtractor.ProgressChanged -= OnGameDataProgress;
            _gameDataExtractor.StatusChanged -= OnGameDataStatus;
            btnExtractGameData.IsEnabled = true;
        }
    }

    private void OnGameDataProgress(int pct, string detail)
    {
        Dispatcher.BeginInvoke(() =>
        {
            pbGameData.Value = pct;
            txtGameDataPct.Text = $"{pct}%";
            txtGameDataDetail.Text = detail;
            Log($"[GameData] {pct}% {detail}");
        });
    }

    private void OnGameDataStatus(string status)
    {
        Dispatcher.BeginInvoke(() =>
        {
            txtGameDataStatus.Text = status;
            Log($"[GameData] {status}");
        });
    }

    private void UpdateSearchIndexStatus()
    {
        if (_gameDataExtractor == null) return;
        var parts = new List<string>();
        var count = _gameDataExtractor.GetItemIndexCount();
        parts.Add($"アイテム: {count}件");
        parts.Add("FTS5: 有効");
        parts.Add(_gameDataExtractor.HasVectorIndex() ? "ベクトル検索: 有効" : "ベクトル検索: 未構築");
        txtSearchIndexStatus.Text = string.Join(" | ", parts);
    }

    private async void BuildVectorIndex_Click(object sender, RoutedEventArgs e)
    {
        if (_gameDataExtractor == null)
        {
            MessageBox.Show("GameDataExtractor が初期化されていません。", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (_gameDataExtractor.GetItemIndexCount() == 0)
        {
            MessageBox.Show("先にインデックス構築を実行してください。", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var backends = App.Config.Translation.Backends.Where(b => IsChatUsable(b)).ToList();
        if (backends.Count == 0)
        {
            MessageBox.Show("AI バックエンドが設定されていません。", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var ollamaBackend = backends.FirstOrDefault(b => b.Type.Equals("Ollama", StringComparison.OrdinalIgnoreCase));
        var openaiBackend = backends.FirstOrDefault(b => b.Type.Equals("OpenAI", StringComparison.OrdinalIgnoreCase));
        var geminiBackend = backends.FirstOrDefault(b => b.Type.Equals("Gemini", StringComparison.OrdinalIgnoreCase));
        var embeddingBackend = ollamaBackend ?? openaiBackend ?? geminiBackend;

        if (embeddingBackend == null)
        {
            MessageBox.Show("エンベディング対応のバックエンド (Ollama/OpenAI/Gemini) が必要です。", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var modelName = embeddingBackend.Type.Equals("Ollama", StringComparison.OrdinalIgnoreCase)
            ? "nomic-embed-text" : embeddingBackend.Model;

        if (embeddingBackend.Type.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var baseUrl = (embeddingBackend.BaseUrl ?? "http://localhost:11434").TrimEnd('/');
                var tagsJson = await http.GetStringAsync($"{baseUrl}/api/tags");
                using var tagsDoc = System.Text.Json.JsonDocument.Parse(tagsJson);
                var models = tagsDoc.RootElement.GetProperty("models");
                bool found = false;
                foreach (var m in models.EnumerateArray())
                {
                    var name = m.GetProperty("name").GetString() ?? "";
                    if (name.StartsWith("nomic-embed-text", StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    MessageBox.Show(
                        $"Ollama サーバー ({baseUrl}) に nomic-embed-text モデルが見つかりません。\n\n" +
                        "以下のコマンドでインストールしてください:\n" +
                        "  ollama pull nomic-embed-text\n\n" +
                        "インストール後に再度お試しください。",
                        "モデル未検出", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            catch (Exception ex)
            {
                var baseUrl = (embeddingBackend.BaseUrl ?? "http://localhost:11434").TrimEnd('/');
                MessageBox.Show(
                    $"Ollama サーバー ({baseUrl}) に接続できません。\n\n" +
                    $"エラー: {ex.Message}\n\n" +
                    "Ollama が起動しているか確認してください。",
                    "接続エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        var result = MessageBox.Show(
            $"ベクトル検索インデックスを構築します。\n\n" +
            $"バックエンド: {embeddingBackend.Name} ({embeddingBackend.Type})\n" +
            $"モデル: {modelName}\n" +
            $"対象: {_gameDataExtractor.GetItemIndexCount()} アイテム\n" +
            $"予測時間: 約2分15秒\n\n" +
            "バックグラウンドで実行します。続行しますか？",
            "ベクトル検索構築", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        var embConfig = new BackendConfig
        {
            Type = embeddingBackend.Type,
            ApiKey = embeddingBackend.ApiKey,
            BaseUrl = embeddingBackend.BaseUrl,
            Model = modelName,
            Name = embeddingBackend.Name
        };

        btnBuildVectorIndex.IsEnabled = false;
        btnExtractGameData.IsEnabled = false;
        _gameDataExtractor.ProgressChanged += OnGameDataProgress;
        _gameDataExtractor.StatusChanged += OnGameDataStatus;

        try
        {
            await Task.Run(() => _gameDataExtractor.BuildVectorIndexAsync(embConfig));
            UpdateSearchIndexStatus();
            MessageBox.Show("ベクトル検索インデックスの構築が完了しました。", "完了", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            txtGameDataStatus.Text = "ベクトル構築失敗";
            MessageBox.Show($"ベクトルインデックス構築エラー:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _gameDataExtractor.ProgressChanged -= OnGameDataProgress;
            _gameDataExtractor.StatusChanged -= OnGameDataStatus;
            btnBuildVectorIndex.IsEnabled = true;
            btnExtractGameData.IsEnabled = true;
        }
    }

    private async void FetchWikiMissions_Click(object sender, RoutedEventArgs e)
    {
        if (_gameDataExtractor == null)
        {
            MessageBox.Show("GameDataExtractor が初期化されていません。", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        btnFetchWikiMissions.IsEnabled = false;
        _gameDataExtractor.ProgressChanged += OnGameDataProgress;
        _gameDataExtractor.StatusChanged += OnGameDataStatus;

        try
        {
            await _gameDataExtractor.FetchWikiMissionsAsync();
            MessageBox.Show("Wiki ミッションデータの取得が完了しました。", "完了", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            txtGameDataStatus.Text = "Wiki ミッション取得失敗";
            MessageBox.Show($"Wiki ミッション取得エラー:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _gameDataExtractor.ProgressChanged -= OnGameDataProgress;
            _gameDataExtractor.StatusChanged -= OnGameDataStatus;
            btnFetchWikiMissions.IsEnabled = true;
        }
    }

    // === Web Server ===

    private async void WebServer_Click(object sender, RoutedEventArgs e)
    {
        if (_webServer?.IsRunning == true)
        {
            await Task.Run(() => _webServer.Stop());
            btnWebServer.Content = "サーバー起動";
            btnWebServer.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x4A, 0x90, 0xD9));
            txtWebServerInfo.Text = "停止中";
            return;
        }
        await StartWebServerAsync();
    }

    private async Task StartWebServerAsync()
    {
        if (!int.TryParse(txtWebPort.Text.Trim(), out var port)) port = 8099;
        if (!int.TryParse(txtWebHttpsPort.Text.Trim(), out var httpsPort)) httpsPort = 8100;
        App.Config.WebServerPort = port;
        App.Config.WebServerHttpsPort = httpsPort;

        _webServer?.Dispose();
        _webServer = new ChatWebServer();

        _webServer.SetMessageHandler(async text =>
        {
            BackendConfig? backend = null;
            BackendConfig? verifyBackend = null;
            List<ChatMessage> history = null!;
            List<BackendConfig> consultBackends = null!;

            Dispatcher.Invoke(() =>
            {
                backend = GetSelectedChatBackend();
                consultBackends = GetCheckedConsultBackends();
                verifyBackend = GetVerifyBackend();
                _chatHistory.Add(new ChatMessage { Role = "user", Content = text });
                history = _chatHistory.ToList();
            });

            // Check for pending remember (user selecting a number)
            var pendingResult = ChatService.TryCompletePendingRemember(text);
            if (pendingResult != null)
            {
                Dispatcher.Invoke(() =>
                    _chatHistory.Add(new ChatMessage { Role = "assistant", Content = pendingResult }));
                return pendingResult;
            }

            if (backend == null) return "バックエンドが選択されていません。アプリのチャットタブでバックエンドを選んでください。";

            var useSkills = backend.SupportsSkills;
            var response = await ChatService.SendChatAsync(backend, history, useSkills);
            var primaryResponse = response;

            if (consultBackends.Count > 0)
            {
                if (_webServer?.IsRunning == true)
                    _ = _webServer.BroadcastTypingAsync("📡 外部 AI に相談中...", "わからなかったのでもう少し調べます。");

                var supplements = await ChatService.ConsultExternalAIsAsync(text, response, consultBackends);
                var sb = new System.Text.StringBuilder(response);
                foreach (var (name, sup) in supplements)
                {
                    if (!string.IsNullOrWhiteSpace(sup) && !sup.Contains("補足はありません"))
                        sb.Append($"\n\n---\n📡 **{name}** の補足:\n{sup}");
                }
                response = sb.ToString();
            }

            // Verification agent for web chat
            if (verifyBackend != null)
            {
                bool userRequestedVerify = text.Contains("検証");
                bool responseInsufficient = ChatService.IsResponseInsufficient(primaryResponse);
                if (userRequestedVerify || responseInsufficient)
                {
                    var verifySpeak = responseInsufficient
                        ? "回答が不十分なようなので検証エージェントに確認します。"
                        : "結果が出たので検証してもらいます。";
                    if (_webServer?.IsRunning == true)
                        _ = _webServer.BroadcastTypingAsync("🔍 検証エージェントに確認中...", verifySpeak);

                    var verifyResult = await ChatService.VerifyWithExternalAIAsync(text, response, verifyBackend);
                    if (!string.IsNullOrWhiteSpace(verifyResult))
                    {
                        response += $"\n\n---\n🔍 **検証 ({verifyBackend.Name}/{verifyBackend.Model})**:\n{verifyResult}";

                        // Auto-save knowledge from web chat verification (no dialog)
                        if (!verifyResult.Contains("検証OK") && _gameDataExtractor != null)
                        {
                            var knowledgeText = ChatService.ExtractKnowledgeSummary(text, verifyResult);
                            if (!string.IsNullOrWhiteSpace(knowledgeText))
                            {
                                try
                                {
                                    var qs = new GameDataQueryService(_gameDataExtractor.DbPath);
                                    var (kid, kisDup) = qs.AddKnowledgeSafe(knowledgeText, "term");
                                    qs.Dispose();
                                    if (!kisDup)
                                    {
                                        Log($"[Knowledge] Web検証結果を自動保存: {knowledgeText.Length} chars");
                                        response += "\n\n💾 検証結果をナレッジに自動保存しました。";
                                    }
                                    else
                                    {
                                        Log($"[Knowledge] Web検証結果: 類似ナレッジ既存 id={kid}");
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
            }

            Dispatcher.Invoke(() =>
            {
                _chatHistory.Add(new ChatMessage { Role = "assistant", Content = response });
            });

            return response;
        });

        _webServer.MessageReceived += (text, isUser) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                _chatBubbles.Add(new ChatBubble { Text = text, IsUser = isUser });
                ScrollChatToBottom();
            });
        };

        _webServer.HistoryCleared += () =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                _chatBubbles.Clear();
                _chatHistory.Clear();
                _chatBubbles.Add(new ChatBubble { Text = "履歴がクリアされました", IsUser = false });
            });
        };

        try
        {
            await Task.Run(() => SslCertHelper.EnsureFirewallRules(port, httpsPort));
            await _webServer.StartAsync(port, httpsPort);
            btnWebServer.Content = "サーバー停止";
            btnWebServer.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xD9, 0x4A, 0x4A));

            UpdateWebServerUrls(port);
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000);
                Dispatcher.BeginInvoke(() => UpdateWebServerUrls(port));
            });
        }
        catch (Exception ex)
        {
            txtWebServerInfo.Text = $"起動失敗: {ex.Message}";
            MessageBox.Show($"Web サーバー起動エラー:\n{ex.Message}\n\nポート {port} が別のアプリで使われている可能性があります。",
                "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateWebServerUrls(int port)
    {
        var ips = ChatWebServer.GetLocalIpAddresses();
        var hp = _webServer?.HttpsPort > 0 ? _webServer.HttpsPort : App.Config.WebServerHttpsPort;
        var httpsReady = _webServer?.HttpsPort > 0;
        var sb = new StringBuilder();
        sb.AppendLine($"PC: http://localhost:{port}/");
        foreach (var ip in ips)
            sb.AppendLine($"LAN: http://{ip}:{port}/");
        sb.AppendLine($"HTTPS PC: https://localhost:{hp}/  {(httpsReady ? "(稼働中)" : "(起動中...)")}");
        foreach (var ip in ips)
            sb.AppendLine($"HTTPS LAN: https://{ip}:{hp}/");
        sb.AppendLine($"証明書DL: http://{(ips.Length > 0 ? ips[0] : "localhost")}:{port}/cert");
        sb.Append("スマホ/マイク利用は HTTPS の URL を使用してください");
        txtWebServerInfo.Text = sb.ToString();
    }

    private BackendConfig? GetSelectedChatBackend()
    {
        if (cmbChatBackend.SelectedIndex < 0) return null;
        var usable = App.Config.Translation.Backends.Where(b => IsChatUsable(b)).ToList();
        return cmbChatBackend.SelectedIndex < usable.Count ? usable[cmbChatBackend.SelectedIndex] : null;
    }

    private readonly HashSet<string> _consultChecked = new();
    private ComboBox? _cmbVerifyBackend;

    private async void CmbChatBackend_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var backend = GetSelectedChatBackend();
        if (backend == null) return;

        bool skillsAvailable = backend.SupportsSkills;

        if (backend.Type.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
        {
            skillsAvailable = await CheckOllamaSkillSupportAsync(backend);
        }

        if (skillsAvailable)
        {
            chkFetchScData.IsEnabled = true;
            chkFetchScData.IsChecked = true;
            chkFetchScData.Content = "スキル使用 (API/DB検索)";
        }
        else
        {
            chkFetchScData.IsChecked = false;
            chkFetchScData.IsEnabled = false;
            chkFetchScData.Content = "スキル非対応";
        }

        RefreshConsultCheckboxes(backend);
    }

    private void RefreshConsultCheckboxes(BackendConfig? primary)
    {
        if (pnlConsultBackends == null) return;
        // Keep only the first label TextBlock ("📡 外部AI相談:")
        while (pnlConsultBackends.Children.Count > 1)
            pnlConsultBackends.Children.RemoveAt(pnlConsultBackends.Children.Count - 1);

        var usableOthers = new List<BackendConfig>();
        foreach (var b in App.Config.Translation.Backends)
        {
            if (!IsChatUsable(b)) continue;
            if (primary != null && b.Name == primary.Name && b.Model == primary.Model) continue;
            usableOthers.Add(b);
        }

        // Add consultation checkboxes
        foreach (var b in usableOthers)
        {
            var key = $"{b.Name}/{b.Model}";
            var cb = new System.Windows.Controls.CheckBox
            {
                Content = $"{b.Name} ({b.Model})",
                Tag = b,
                IsChecked = _consultChecked.Contains(key),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                FontSize = 12
            };
            cb.Checked += (_, _) => _consultChecked.Add(key);
            cb.Unchecked += (_, _) => _consultChecked.Remove(key);
            pnlConsultBackends.Children.Add(cb);
        }

        // Add separator + verify agent ComboBox in the same row
        if (usableOthers.Count > 0)
        {
            pnlConsultBackends.Children.Add(new TextBlock
            {
                Text = "│",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0),
                FontSize = 12,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#CCC"))
            });
        }

        pnlConsultBackends.Children.Add(new TextBlock
        {
            Text = "🔍 検証:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            FontSize = 12,
            Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#666"))
        });

        var prevVerify = _cmbVerifyBackend?.SelectedItem as BackendConfig;
        _cmbVerifyBackend = new ComboBox
        {
            Width = 180,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11
        };
        _cmbVerifyBackend.Items.Add("なし");
        foreach (var b in usableOthers)
            _cmbVerifyBackend.Items.Add(b);

        _cmbVerifyBackend.SelectionChanged += (_, _) =>
            ChatService.SetVerifyBackend(_cmbVerifyBackend.SelectedItem as BackendConfig);

        if (prevVerify != null && _cmbVerifyBackend.Items.Contains(prevVerify))
            _cmbVerifyBackend.SelectedItem = prevVerify;
        else
            _cmbVerifyBackend.SelectedIndex = 0;

        ChatService.SetVerifyBackend(_cmbVerifyBackend.SelectedItem as BackendConfig);
        ChatService.SetVerifyBackendCandidates(usableOthers);
        pnlConsultBackends.Children.Add(_cmbVerifyBackend);

        pnlConsultBackends.Visibility = usableOthers.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private List<BackendConfig> GetCheckedConsultBackends()
    {
        var result = new List<BackendConfig>();
        for (int i = 1; i < pnlConsultBackends.Children.Count; i++)
        {
            if (pnlConsultBackends.Children[i] is System.Windows.Controls.CheckBox cb && cb.IsChecked == true && cb.Tag is BackendConfig b)
                result.Add(b);
        }
        return result;
    }

    private BackendConfig? GetVerifyBackend()
    {
        return _cmbVerifyBackend?.SelectedItem as BackendConfig;
    }

    private static async Task<bool> CheckOllamaSkillSupportAsync(BackendConfig backend)
    {
        try
        {
            var baseUrl = string.IsNullOrWhiteSpace(backend.BaseUrl) ? "http://localhost:11434" : backend.BaseUrl.TrimEnd('/');
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var resp = await http.GetStringAsync($"{baseUrl}/api/version");
            using var doc = System.Text.Json.JsonDocument.Parse(resp);
            var versionStr = doc.RootElement.GetProperty("version").GetString() ?? "";
            Console.WriteLine($"[Chat] Ollama version: {versionStr}");
            if (Version.TryParse(versionStr.Split('-')[0], out var ver))
                return ver >= new Version(0, 3, 0);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Chat] Ollama version check failed: {ex.Message}");
            return false;
        }
    }

    private async void ChatSend_Click(object sender, RoutedEventArgs e) => await SendChatMessageAsync();

    private async void ChatInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                var tb = (TextBox)sender;
                var caret = tb.CaretIndex;
                tb.Text = tb.Text.Insert(caret, Environment.NewLine);
                tb.CaretIndex = caret + Environment.NewLine.Length;
                e.Handled = true;
            }
            else
            {
                e.Handled = true;
                await SendChatMessageAsync();
            }
        }
    }

    private async Task SendChatMessageAsync()
    {
        if (_chatSending) return;
        var text = txtChatInput.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        // Check for pending remember (user selecting a number)
        var pendingResult = ChatService.TryCompletePendingRemember(text);
        if (pendingResult != null)
        {
            txtChatInput.Text = "";
            _chatBubbles.Add(new ChatBubble { Text = text, IsUser = true });
            _chatBubbles.Add(new ChatBubble { Text = pendingResult, IsUser = false });
            _chatHistory.Add(new ChatMessage { Role = "user", Content = text });
            _chatHistory.Add(new ChatMessage { Role = "assistant", Content = pendingResult });
            ScrollChatToBottom();
            if (_webServer?.IsRunning == true)
            {
                _ = _webServer.BroadcastMessageAsync(text, true);
                _ = _webServer.BroadcastMessageAsync(pendingResult, false);
            }
            return;
        }

        var backend = GetSelectedChatBackend();
        if (backend == null)
        {
            var enabledCount = App.Config.Translation.Backends.Count(b => b.Enabled);
            if (enabledCount == 0)
            {
                MessageBox.Show("AI バックエンドが設定されていません。\n\n設定タブの「AI 設定を開く」から、Claude / Gemini / Ollama のいずれかを有効にして API キーを設定してください。",
                    "AI 設定が必要です", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show("AI バックエンドが選択されていません。\nチャットタブ上部のプルダウンからバックエンドを選択してください。",
                    "バックエンド未選択", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return;
        }

        App.Config.LastChatBackend = cmbChatBackend.SelectedItem as string ?? "";
        SaveConfigToFile();

        _chatSending = true;
        btnChatSend.IsEnabled = false;
        txtChatInput.Text = "";

        _chatBubbles.Add(new ChatBubble { Text = text, IsUser = true });
        _chatHistory.Add(new ChatMessage { Role = "user", Content = text });
        ScrollChatToBottom();
        if (_webServer?.IsRunning == true)
            _ = _webServer.BroadcastMessageAsync(text, true);

        _chatBubbles.Add(new ChatBubble { Text = "考え中...", IsUser = false });
        ScrollChatToBottom();

        try
        {
            var useSkills = chkFetchScData.IsChecked == true && backend.SupportsSkills;
            ReplaceLast(new ChatBubble
            {
                Text = useSkills ? "AI がスキルを使って回答を生成中..." : "AI が回答を生成中...",
                IsUser = false
            });
            if (_webServer?.IsRunning == true)
                _ = _webServer.BroadcastTypingAsync("AI が回答を生成中...");
            Log($"[Chat] AI 応答生成開始: backend={backend.Name}/{backend.Model}, skills={useSkills}");
            var swAi = System.Diagnostics.Stopwatch.StartNew();

            var response = await ChatService.SendChatAsync(backend, _chatHistory, useSkills);
            var primaryResponse = response; // preserve for insufficient check
            Log($"[Chat] AI 応答完了: {swAi.ElapsedMilliseconds}ms, {response.Length} chars");

            var consultBackends = GetCheckedConsultBackends();
            if (consultBackends.Count > 0)
            {
                const string consultSpeak = "わからなかったのでもう少し調べます。";
                ReplaceLast(new ChatBubble { Text = response + "\n\n📡 外部 AI に相談中...", IsUser = false });
                if (_webServer?.IsRunning == true)
                    _ = _webServer.BroadcastTypingAsync("📡 外部 AI に相談中...", consultSpeak);

                var supplements = await ChatService.ConsultExternalAIsAsync(text, response, consultBackends);
                var sb = new System.Text.StringBuilder(response);
                foreach (var (name, sup) in supplements)
                {
                    if (!string.IsNullOrWhiteSpace(sup) && !sup.Contains("補足はありません"))
                        sb.Append($"\n\n---\n📡 **{name}** の補足:\n{sup}");
                }
                response = sb.ToString();
            }

            // Verification agent: invoke when user says "検証" or response seems insufficient
            var verifyBackend = GetVerifyBackend();
            if (verifyBackend != null)
            {
                bool userRequestedVerify = text.Contains("検証");
                bool responseInsufficient = ChatService.IsResponseInsufficient(primaryResponse);
                if (userRequestedVerify || responseInsufficient)
                {
                    var verifySpeak = responseInsufficient
                        ? "回答が不十分なようなので検証エージェントに確認します。"
                        : "結果が出たので検証してもらいます。";
                    ReplaceLast(new ChatBubble { Text = response + "\n\n🔍 検証エージェントに確認中...", IsUser = false });
                    if (_webServer?.IsRunning == true)
                        _ = _webServer.BroadcastTypingAsync("🔍 検証エージェントに確認中...", verifySpeak);

                    var verifyResult = await ChatService.VerifyWithExternalAIAsync(text, response, verifyBackend);
                    if (!string.IsNullOrWhiteSpace(verifyResult))
                    {
                        response += $"\n\n---\n🔍 **検証 ({verifyBackend.Name}/{verifyBackend.Model})**:\n{verifyResult}";

                        // Offer to save knowledge if verification found corrections
                        if (!verifyResult.Contains("検証OK") && _gameDataExtractor != null)
                        {
                            var knowledgeText = ChatService.ExtractKnowledgeSummary(text, verifyResult);
                            if (!string.IsNullOrWhiteSpace(knowledgeText))
                                OfferKnowledgeSave(knowledgeText);
                        }
                    }
                }
            }

            ReplaceLast(new ChatBubble { Text = response, IsUser = false });
            _chatHistory.Add(new ChatMessage { Role = "assistant", Content = response });
            if (_webServer?.IsRunning == true)
            {
                _ = _webServer.BroadcastMessageAsync(response, false);
                _ = _webServer.BroadcastTypingAsync("");
            }
        }
        catch (Exception ex)
        {
            Log($"[Chat] エラー: {ex.Message}");
            ReplaceLast(new ChatBubble { Text = $"エラー: {ex.Message}", IsUser = false, IsError = true });
        }
        finally
        {
            _chatSending = false;
            btnChatSend.IsEnabled = true;
            ScrollChatToBottom();
        }
    }

    private void ChatCopyAll_Click(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        foreach (var bubble in _chatBubbles)
        {
            var label = bubble.IsUser ? "Q" : "A";
            sb.AppendLine($"**{label}:** {bubble.Text}");
            sb.AppendLine();
        }
        if (sb.Length > 0)
        {
            Clipboard.SetText(sb.ToString());
            MessageBox.Show("チャット履歴をクリップボードにコピーしました。", "コピー完了");
        }
    }

    private void ChatClear_Click(object sender, RoutedEventArgs e)
    {
        _chatBubbles.Clear();
        _chatHistory.Clear();
        _chatBubbles.Add(new ChatBubble
        {
            Text = "Star Citizen について質問してください。\nUEX API・SC Trade Tools・Wiki・ゲームファイルから最新データを取得して回答します。",
            IsUser = false
        });
        if (_webServer?.IsRunning == true)
            _ = _webServer.BroadcastClearAsync();
    }

    private void KnowledgeManage_Click(object sender, RoutedEventArgs e)
    {
        if (_gameDataExtractor == null) { MessageBox.Show("先にゲームデータを読み込んでください。"); return; }
        var qs = new GameDataQueryService(_gameDataExtractor.DbPath);
        try
        {
            var verifyBackend = GetVerifyBackend();
            var win = new KnowledgeWindow(qs, verifyBackend) { Owner = this };
            win.ShowDialog();
        }
        finally { qs.Dispose(); }
    }

    private void OfferKnowledgeSave(string knowledgeText)
    {
        _chatBubbles.Add(new ChatBubble
        {
            Text = $"💾 検証で新しい情報が見つかりました。ナレッジに保存しますか？\n\n{knowledgeText}",
            IsUser = false
        });
        ScrollChatToBottom();

        var result = MessageBox.Show(
            $"検証結果をナレッジに保存しますか？\n\n{knowledgeText}",
            "ナレッジ保存", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes && _gameDataExtractor != null)
        {
            try
            {
                var qs = new GameDataQueryService(_gameDataExtractor.DbPath);
                var (kid2, kisDup2) = qs.AddKnowledgeSafe(knowledgeText, "term");
                qs.Dispose();
                if (!kisDup2)
                {
                    Log($"[Knowledge] 検証結果を保存: {knowledgeText.Length} chars");
                    _chatBubbles.Add(new ChatBubble { Text = "✅ ナレッジに保存しました。次回から活用されます。", IsUser = false });
                }
                else
                {
                    Log($"[Knowledge] 類似ナレッジ既存 id={kid2}");
                    _chatBubbles.Add(new ChatBubble { Text = $"ℹ️ 類似するナレッジが既にあります (ID:{kid2})。重複保存をスキップしました。", IsUser = false });
                }
            }
            catch (Exception ex)
            {
                Log($"[Knowledge] 保存エラー: {ex.Message}");
                _chatBubbles.Add(new ChatBubble { Text = $"❌ 保存エラー: {ex.Message}", IsUser = false, IsError = true });
            }
        }
        else
        {
            // Remove the offer bubble
            if (_chatBubbles.Count > 0 && _chatBubbles[^1].Text.StartsWith("💾"))
                _chatBubbles.RemoveAt(_chatBubbles.Count - 1);
        }
        ScrollChatToBottom();
    }

    private void ReplaceLast(ChatBubble bubble)
    {
        if (_chatBubbles.Count > 0)
            _chatBubbles.RemoveAt(_chatBubbles.Count - 1);
        _chatBubbles.Add(bubble);
    }

    private void ScrollChatToBottom()
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            svChat.ScrollToEnd();
        });
    }

    private void RestoreSavedShipSelection()
    {
        var savedName = App.Config.TradeShipName;
        if (string.IsNullOrEmpty(savedName) || cmbTradeShip.ItemsSource == null) return;
        foreach (var item in cmbTradeShip.ItemsSource)
        {
            if (item is ShipInfo s && s.Name.Contains(savedName, StringComparison.OrdinalIgnoreCase))
            {
                cmbTradeShip.SelectedItem = item;
                return;
            }
        }
    }

    // === Window State ===

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _captureService?.Dispose();

        var config = App.Config;
        if (WindowState == WindowState.Maximized)
        {
            config.WindowMaximized = true;
        }
        else
        {
            config.WindowMaximized = false;
            config.WindowLeft = Left;
            config.WindowTop = Top;
            config.WindowWidth = Width;
            config.WindowHeight = Height;
        }
        SaveConfigToFile();
    }

    private void RestoreWindowState(AppConfig config)
    {
        if (config.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
        else if (!double.IsNaN(config.WindowWidth) && config.WindowWidth > 100 &&
                 !double.IsNaN(config.WindowHeight) && config.WindowHeight > 100)
        {
            Width = config.WindowWidth;
            Height = config.WindowHeight;

            if (!double.IsNaN(config.WindowLeft) && !double.IsNaN(config.WindowTop))
            {
                var left = config.WindowLeft;
                var top = config.WindowTop;
                // Simple bounds check against virtual screen
                var vw = SystemParameters.VirtualScreenWidth;
                var vh = SystemParameters.VirtualScreenHeight;
                var vl = SystemParameters.VirtualScreenLeft;
                var vt = SystemParameters.VirtualScreenTop;
                if (left >= vl && left + Width <= vl + vw && top >= vt && top + Height <= vt + vh)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = left;
                    Top = top;
                }
            }
        }
    }

    private static void SelectComboByContent(ComboBox combo, string? content)
    {
        if (string.IsNullOrEmpty(content)) return;
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem ci && ci.Content?.ToString() == content)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
    }

    // === Ship Management (船舶管理) ===

    private void RefreshMyShips()
    {
        _tradeService.SetCacheDir(WorkDir);
        _tradeService.LoadMyShips();
        dgMyShips.ItemsSource = null;
        dgMyShips.ItemsSource = _tradeService.MyShips;
        txtMyShipStatus.Text = $"所持船: {_tradeService.MyShips.Count} 隻";
        RefreshCommodityShipCombo();
    }

    private void RefreshCommodityShipCombo()
    {
        try
        {
            cmbTradeShip.SelectionChanged -= TradeShip_Changed;
            var items = new List<object>();
            foreach (var my in _tradeService.MyShips)
                items.Add(new ShipInfo { Name = $"★ {my.Name}", Manufacturer = my.Manufacturer, Scu = my.Scu });
            foreach (var s in _tradeService.Ships)
                items.Add(s);
            cmbTradeShip.ItemsSource = items;
            cmbTradeShip.DisplayMemberPath = "DisplayName";
            cmbTradeShip.SelectionChanged += TradeShip_Changed;
        }
        catch { }
    }

    private void ShipSearch_Click(object sender, RoutedEventArgs e) => SearchShips();
    private void ShipSearch_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) SearchShips();
    }

    private void SearchShips()
    {
        if (_tradeService.Ships.Count == 0)
        {
            if (_tradeService.IsFetching)
                txtMyShipStatus.Text = "UEX船データを取得中...しばらくお待ちください";
            else
                txtMyShipStatus.Text = "船データがありません。コモディティタブの [価格更新] を実行してください";
            return;
        }

        var query = txtShipSearch.Text.Trim();
        if (string.IsNullOrEmpty(query) || query.Length < 2)
        {
            cmbAddShip.ItemsSource = _tradeService.Ships;
            cmbAddShip.IsDropDownOpen = true;
            txtMyShipStatus.Text = $"全 {_tradeService.Ships.Count} 件 (2文字以上で絞り込み)";
            return;
        }
        var results = _tradeService.Ships
            .Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        s.Manufacturer.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(50).ToList();
        cmbAddShip.ItemsSource = results;
        cmbAddShip.IsDropDownOpen = results.Count > 0;
        txtMyShipStatus.Text = results.Count > 0
            ? $"検索結果: {results.Count} 件 — 候補から選択してください"
            : $"「{query}」に一致する船が見つかりません (全 {_tradeService.Ships.Count} 件中)";
    }

    private void AddShip_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (cmbAddShip.SelectedItem is ShipInfo ship)
        {
            txtAddShipName.Text = ship.Name;
            txtAddShipMfr.Text = ship.Manufacturer;
            txtAddShipScu.Text = ship.Scu.ToString();
        }
    }

    private void AddMyShip_Click(object sender, RoutedEventArgs e)
    {
        var name = txtAddShipName.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show("船名を入力してください。検索で候補を選択するか、直接入力してください。", "入力エラー");
            return;
        }

        // @vehicle_Name 解決
        name = _tradeService.ResolveVehicleName(name);

        var mfr = txtAddShipMfr.Text.Trim();
        int.TryParse(txtAddShipScu.Text.Trim(), out var scu);
        var notes = txtAddShipNotes.Text.Trim();

        // UEX データから SCU を補完
        if (scu == 0)
        {
            var uex = _tradeService.FindUexShip(name);
            if (uex != null)
            {
                scu = uex.Scu;
                if (string.IsNullOrEmpty(mfr)) mfr = uex.Manufacturer;
            }
        }

        _tradeService.AddMyShip(name, mfr, scu, notes);
        RefreshMyShips();
        txtAddShipName.Text = "";
        txtAddShipMfr.Text = "";
        txtAddShipScu.Text = "0";
        txtAddShipNotes.Text = "";
        Log($"[Ship] 追加: {name} ({scu} SCU)");
    }

    private void DeleteMyShip_Click(object sender, RoutedEventArgs e)
    {
        if (dgMyShips.SelectedItem is not MyShipEntry ship) return;
        if (MessageBox.Show($"「{ship.Name}」を削除しますか？", "確認", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;

        _tradeService.DeleteMyShip(ship.Id);
        RefreshMyShips();
        Log($"[Ship] 削除: {ship.Name}");
    }

    private void MyShip_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (dgMyShips.SelectedItem is not MyShipEntry ship) return;

        txtAddShipName.Text = ship.Name;
        txtAddShipMfr.Text = ship.Manufacturer;
        txtAddShipScu.Text = ship.Scu.ToString();
        txtAddShipNotes.Text = ship.Notes;
        txtMyShipStatus.Text = $"編集中: {ship.Name} — 入力欄を変更して [追加] で新規 or 下の更新ボタンで上書き";
    }

    private void UpdateMyShip_Click(object sender, RoutedEventArgs e)
    {
        if (dgMyShips.SelectedItem is not MyShipEntry ship) return;
        var name = txtAddShipName.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;
        int.TryParse(txtAddShipScu.Text.Trim(), out var scu);
        _tradeService.UpdateMyShip(ship.Id, name, txtAddShipMfr.Text.Trim(), scu, txtAddShipNotes.Text.Trim());
        RefreshMyShips();
        Log($"[Ship] 更新: {name} ({scu} SCU)");
    }

    // === Commodity Trade ===

    private void CommodityFilter_Click(object sender, RoutedEventArgs e)
    {
        if (!_tradeService.HasPriceData)
        {
            MessageBox.Show("価格データ未取得です。先に [価格更新] を実行してください。", "データなし");
            return;
        }

        var allNames = _tradeService.GetCommodityNames();
        var win = new Window
        {
            Title = "コモディティ選択",
            Width = 400, Height = 550,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this
        };

        var sp = new StackPanel { Margin = new Thickness(8) };
        var btnAll = new Button { Content = "全選択", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 4, 8) };
        var btnNone = new Button { Content = "全解除", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 4, 8) };
        var btnOk = new Button { Content = "OK", Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(8, 0, 0, 8), FontWeight = FontWeights.Bold, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x19, 0x76, 0xD2)), Foreground = System.Windows.Media.Brushes.White };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal };
        toolbar.Children.Add(btnAll);
        toolbar.Children.Add(btnNone);
        toolbar.Children.Add(btnOk);
        sp.Children.Add(toolbar);

        var listBox = new ListBox { Height = 440 };
        var checkBoxes = new List<CheckBox>();
        foreach (var name in allNames)
        {
            var cb = new CheckBox { Content = name, IsChecked = _selectedCommodities == null || _selectedCommodities.Contains(name), Margin = new Thickness(2) };
            checkBoxes.Add(cb);
            listBox.Items.Add(cb);
        }
        sp.Children.Add(listBox);

        btnAll.Click += (_, _) => checkBoxes.ForEach(cb => cb.IsChecked = true);
        btnNone.Click += (_, _) => checkBoxes.ForEach(cb => cb.IsChecked = false);
        btnOk.Click += (_, _) =>
        {
            var selected = checkBoxes.Where(cb => cb.IsChecked == true).Select(cb => cb.Content.ToString()!).ToHashSet();
            if (selected.Count == allNames.Count)
            {
                _selectedCommodities = null;
                btnCommodityFilter.Content = "コモディティ選択 (全て)";
            }
            else
            {
                _selectedCommodities = selected;
                btnCommodityFilter.Content = $"コモディティ選択 ({selected.Count}/{allNames.Count})";
            }
            win.Close();
        };

        win.Content = sp;
        win.ShowDialog();
    }

    private void TradeRoutes_CellClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (dgTradeRoutes.CurrentItem is not TradeRoute route) return;

        // Determine which column was clicked
        var cell = dgTradeRoutes.CurrentCell;
        var colHeader = cell.Column?.Header?.ToString() ?? "";

        if (colHeader == "購入場所")
            ShowLocationDetail(route.BuyTerminal, route.BuyDisplay);
        else if (colHeader == "売却場所")
            ShowLocationDetail(route.SellTerminal, route.SellDisplay);
        else
            ShowCommodityDetail(route.CommodityName);
    }

    private void ShowCommodityDetail(string commodityName)
    {
        var (buyLocs, sellLocs) = _tradeService.GetCommodityDetail(commodityName);

        dgDetailBuy.ItemsSource = buyLocs.Select(p => new TradeDetailRow
        {
            Location = p.LocationShort.Contains($"({p.StarSystem})") ? p.LocationShort : $"{p.LocationShort} ({p.StarSystem})",
            Price = $"{p.PriceBuy:N1}",
            Stock = p.ScuBuy > 0 ? $"{p.ScuBuy:N0}" : "-",
            Terminal = p.Terminal,
        }).ToList();

        dgDetailSell.ItemsSource = sellLocs.Select(p => new TradeDetailRow
        {
            Location = p.LocationShort.Contains($"({p.StarSystem})") ? p.LocationShort : $"{p.LocationShort} ({p.StarSystem})",
            Price = $"{p.PriceSell:N1}",
            Stock = p.ScuSell > 0 ? $"{p.ScuSell:N0}" : "-",
            Terminal = p.Terminal,
        }).ToList();

        grpDetailLeft.Header = $"購入場所 (安い順) — {commodityName}";
        grpDetailRight.Header = $"売却場所 (高い順) — {commodityName}";
        txtDetailHeader.Text = $"{commodityName} — 購入 {buyLocs.Count} 箇所 / 売却 {sellLocs.Count} 箇所  [購入場所/売却場所クリックでその場所の全商品]";
        grpTradeDetail.Visibility = Visibility.Visible;
    }

    private void ShowLocationDetail(string terminal, string displayName)
    {
        if (string.IsNullOrEmpty(terminal)) return;

        var buyable = _tradeService.GetBuyableAtLocation(terminal);
        var sellable = _tradeService.GetSellableAtLocation(terminal);

        dgDetailBuy.ItemsSource = buyable.Select(p => new TradeDetailRow
        {
            Location = p.CommodityName,
            Price = $"{p.PriceBuy:N1}",
            Stock = p.ScuBuy > 0 ? $"{p.ScuBuy:N0}" : "-",
            Terminal = p.Terminal,
        }).ToList();

        dgDetailSell.ItemsSource = sellable.Select(p => new TradeDetailRow
        {
            Location = p.CommodityName,
            Price = $"{p.PriceSell:N1}",
            Stock = p.ScuSell > 0 ? $"{p.ScuSell:N0}" : "-",
            Terminal = p.Terminal,
        }).ToList();

        grpDetailLeft.Header = $"購入できる商品 — {displayName}";
        grpDetailRight.Header = $"売却できる商品 — {displayName}";
        txtDetailHeader.Text = $"{displayName} — 購入 {buyable.Count} 品 / 売却 {sellable.Count} 品";
        grpTradeDetail.Visibility = Visibility.Visible;
    }

    private void CloseDetail_Click(object sender, RoutedEventArgs e)
    {
        grpTradeDetail.Visibility = Visibility.Collapsed;
    }

    private void DetailBuy_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (dgDetailBuy.SelectedItem is TradeDetailRow row && !string.IsNullOrEmpty(row.Location))
        {
            var name = row.Location;
            if (_tradeService.GetCommodityNames().Contains(name, StringComparer.OrdinalIgnoreCase))
                ShowCommodityDetail(name);
        }
    }

    private void DetailSell_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (dgDetailSell.SelectedItem is TradeDetailRow row && !string.IsNullOrEmpty(row.Location))
        {
            var name = row.Location;
            if (_tradeService.GetCommodityNames().Contains(name, StringComparer.OrdinalIgnoreCase))
                ShowCommodityDetail(name);
        }
    }

    private static double ParseSuffixedNumber(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return 0;
        input = input.Trim().Replace(",", "").Replace("_", "");
        double multiplier = 1;
        if (input.EndsWith("k", StringComparison.OrdinalIgnoreCase)) { multiplier = 1_000; input = input[..^1]; }
        else if (input.EndsWith("m", StringComparison.OrdinalIgnoreCase)) { multiplier = 1_000_000; input = input[..^1]; }
        else if (input.EndsWith("b", StringComparison.OrdinalIgnoreCase)) { multiplier = 1_000_000_000; input = input[..^1]; }
        return double.TryParse(input.Trim(), out var val) ? val * multiplier : 0;
    }

    private string DetectGamePatch()
    {
        try
        {
            var manifestPath = Path.Combine(txtGamePath.Text.Trim(), "build_manifest.id");
            if (File.Exists(manifestPath))
            {
                var content = File.ReadAllText(manifestPath);
                var match = System.Text.RegularExpressions.Regex.Match(content, @"""RequestedP4ChangeNum""\s*""(\d+)""");
                var branchMatch = System.Text.RegularExpressions.Regex.Match(content, @"""Branch""\s*""([^""]+)""");
                var versionMatch = System.Text.RegularExpressions.Regex.Match(content, @"""Data""\s*""[^""]*?(\d+\.\d+)");
                if (versionMatch.Success) return versionMatch.Groups[1].Value;
                if (branchMatch.Success) return branchMatch.Groups[1].Value;
            }
        }
        catch { }
        return "4.0";
    }

    private async Task StartBackgroundTradeFetchAsync()
    {
        _tradeService.OnProgress += msg =>
        {
            try
            {
                Dispatcher.BeginInvoke(() =>
                {
                    txtTradeStatus.Text = $"[自動取得] {msg}";
                    Log($"[Trade] {msg}");
                });
            }
            catch { }
        };

        try
        {
            _tradeService.SetCacheDir(WorkDir);
            _tradeService.GamePatch = DetectGamePatch();
            Log($"[Trade] パッチ: {_tradeService.GamePatch} バックグラウンド価格取得を開始...");
            await Task.Run(async () => await _tradeService.FetchAllDataAsync());
            Log($"[Trade] 取得完了: 価格 {_tradeService.PriceCount:N0} 件, 船 {_tradeService.Ships.Count} 件");
        }
        catch (Exception ex)
        {
            Log($"[Trade] 取得エラー: {ex}");
            try { Dispatcher.Invoke(() => txtTradeStatus.Text = $"自動取得失敗: {ex.Message}"); } catch { }
            return;
        }

        try
        {
            Dispatcher.Invoke(() =>
            {
                _tradeService.LoadMyShips();
                RefreshCommodityShipCombo();
                RestoreSavedShipSelection();
                dgMyShips.ItemsSource = _tradeService.MyShips;
                txtMyShipStatus.Text = $"所持船: {_tradeService.MyShips.Count} 隻 | UEX船データ: {_tradeService.Ships.Count} 件";
                cmbAddShip.ItemsSource = _tradeService.Ships;
                cmbAddShip.DisplayMemberPath = "DisplayName";
                txtTradeStatus.Text = $"価格 {_tradeService.PriceCount:N0} 件 | 船 {_tradeService.Ships.Count} 件 | 所持船 {_tradeService.MyShips.Count} 隻 | 更新: {_tradeService.LastPriceUpdate:HH:mm}";
            });
            ChatService.SetTradeService(_tradeService);
        }
        catch (Exception ex)
        {
            Log($"[Trade] UI更新エラー: {ex}");
            try { Dispatcher.Invoke(() => txtTradeStatus.Text = $"エラー: {ex.Message}"); } catch { }
        }
    }


    private async void TradeRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_tradeService.IsFetching)
        {
            txtTradeStatus.Text = "取得中です...しばらくお待ちください";
            return;
        }

        dgTradeRoutes.ItemsSource = null;
        try
        {
            _tradeService.SetCacheDir(WorkDir);
            _tradeService.GamePatch = DetectGamePatch();
            await Task.Run(async () => await _tradeService.FetchAllDataAsync(force: true));
            Log($"[Trade] 強制取得完了: 船 {_tradeService.Ships.Count}, 価格 {_tradeService.PriceCount}");
            RefreshCommodityShipCombo();
            cmbAddShip.ItemsSource = _tradeService.Ships;
            cmbAddShip.DisplayMemberPath = "DisplayName";
            dgMyShips.ItemsSource = _tradeService.MyShips;
            txtMyShipStatus.Text = $"所持船: {_tradeService.MyShips.Count} 隻 | UEX船データ: {_tradeService.Ships.Count} 件";
            txtTradeStatus.Text = $"価格 {_tradeService.PriceCount:N0} 件 | 船 {_tradeService.Ships.Count} 件 | 所持船 {_tradeService.MyShips.Count} 隻 | 更新: {_tradeService.LastPriceUpdate:HH:mm} (強制取得)";
            ChatService.SetTradeService(_tradeService);
        }
        catch (Exception ex)
        {
            txtTradeStatus.Text = $"エラー: {ex.Message}";
        }
    }

    private void TradeShip_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (cmbTradeShip.SelectedItem is ShipInfo ship)
            txtTradeScu.Text = ship.Scu.ToString();
    }

    private void TradeSearch_Click(object sender, RoutedEventArgs e)
    {
        if (!_tradeService.HasPriceData)
        {
            txtTradeStatus.Text = _tradeService.IsFetching
                ? "バックグラウンドで取得中... 完了までお待ちください"
                : "まず [価格更新] を実行してデータを取得してください";
            return;
        }

        if (!int.TryParse(txtTradeScu.Text.Trim(), out var scu) || scu <= 0)
        {
            MessageBox.Show("積載量 (SCU) を正の整数で入力してください。", "入力エラー");
            return;
        }
        var budget = ParseSuffixedNumber(txtTradeBudget.Text.Trim());
        if (budget <= 0)
        {
            MessageBox.Show("予算 (aUEC) を入力してください。\n例: 1000000, 1M, 500k, 3.5m", "入力エラー");
            return;
        }

        var buySystem = (cmbTradeBuySystem.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "全て";
        var sellSystem = (cmbTradeSellSystem.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "全て";
        var excludeOutposts = chkExcludeOutpost.IsChecked == true;
        var loadingDockOnly = chkLoadingDockOnly.IsChecked == true;
        var excludeLowStock = chkExcludeLowStock.IsChecked == true;

        // Save search params
        App.Config.TradeShipName = (cmbTradeShip.SelectedItem is ShipInfo si) ? si.Name : "";
        App.Config.TradeScu = scu;
        App.Config.TradeBudget = txtTradeBudget.Text.Trim();
        App.Config.TradeBuySystem = buySystem;
        App.Config.TradeSellSystem = sellSystem;

        var routes = _tradeService.CalculateBestRoutes(budget, scu, buySystem, sellSystem,
            excludeOutposts, loadingDockOnly, excludeLowStock, _selectedCommodities, topN: 20);

        dgTradeRoutes.ItemsSource = routes;
        if (routes.Count > 0)
        {
            var best = routes[0];
            txtTradeInfo.Text = $"上位 {routes.Count} ルート | 最高: {best.CommodityName} ({best.TotalProfitDisplay} aUEC, ROI {best.RoiDisplay})";
        }
        else
        {
            txtTradeInfo.Text = "条件に合うルートが見つかりません。フィルタや予算を変更してみてください。";
        }
        txtTradeStatus.Text = $"更新: {_tradeService.LastPriceUpdate:HH:mm} | {buySystem} → {sellSystem} | {scu} SCU | 予算 {budget:N0}";
    }

    // === Screen Capture / OCR / UEX ===

    private void InitCapture()
    {
        _captureService = new ScreenCaptureService();
        _captureService.OnScreenCaptured += png =>
            Dispatcher.BeginInvoke(() => _ = ProcessCaptureAsync(png));
        _captureService.OnLog += msg =>
            Dispatcher.BeginInvoke(() => txtCaptureStatus.Text = msg);

        _uexSubmitService = new UexSubmissionService();
        _uexSubmitService.OnLog += msg =>
            Dispatcher.BeginInvoke(() => txtSubmitStatus.Text = msg);

        // Register global hotkey
        var hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(_captureService.WndProc);
        _captureService.Register(hwnd);

        // Populate terminal combo
        PopulateCaptureTerminals();
    }

    private void PopulateCaptureTerminals()
    {
        if (!_tradeService.HasPriceData) return;

        var terminals = _tradeService.GetTerminals()
            .OrderBy(kv => kv.Key)
            .Select(kv => new KeyValuePair<string, int>(kv.Key, kv.Value.Id))
            .ToList();
        cmbCaptureTerminal.ItemsSource = terminals;
    }

    private async Task ProcessCaptureAsync(byte[] pngImage)
    {
        txtCaptureStatus.Text = "OCR処理中...";
        txtOcrTiming.Text = "";
        txtOcrConfidence.Text = "";

        // Show preview
        var bitmapImage = new BitmapImage();
        bitmapImage.BeginInit();
        bitmapImage.StreamSource = new MemoryStream(pngImage);
        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
        bitmapImage.EndInit();
        bitmapImage.Freeze();
        imgCapturePreview.Source = bitmapImage;

        IOcrEngine ocrEngine = new WindowsOcrEngine();

        try
        {
            var ocrResult = await ocrEngine.RecognizeAsync(pngImage);
            txtOcrConfidence.Text = $"{ocrResult.Confidence:P0}";
            txtOcrTiming.Text = $"{ocrResult.ProcessingTime.TotalMilliseconds:N0}ms";

            if (!_tradeService.HasPriceData)
            {
                txtCaptureStatus.Text = "価格データ未取得。コモディティタブで [価格更新] を実行してください";
                return;
            }

            // Build commodity dictionary with Japanese names
            var dictionary = new CommodityDictionary();
            var translationDbPath = Path.Combine(WorkDir, "translations.db");
            dictionary.BuildFromTradeService(_tradeService, translationDbPath);

            var parser = new TradingTerminalParser(
                _tradeService.GetTerminalNameToIdMap(),
                dictionary);

            _currentCapture = parser.Parse(ocrResult);
            if (_currentCapture != null)
            {
                _currentCapture.ScreenshotPng = pngImage;
                DisplayCaptureResult(_currentCapture);
                var matchedCount = _currentCapture.Commodities.Count(c => c.IsMatched);
                txtCaptureStatus.Text = $"認識完了: {_currentCapture.Commodities.Count} 品目 ({matchedCount} マッチ)";
            }
            else
            {
                txtCaptureStatus.Text = "トレードターミナルを認識できませんでした。ゲーム画面でトレードターミナルを表示した状態でキャプチャしてください。";
            }
        }
        catch (Exception ex)
        {
            txtCaptureStatus.Text = $"OCRエラー: {ex.Message}";
        }
    }

    private void DisplayCaptureResult(TerminalCaptureData data)
    {
        // Set terminal in combo
        if (cmbCaptureTerminal.ItemsSource is List<KeyValuePair<string, int>> terminals)
        {
            var match = terminals.FirstOrDefault(t =>
                t.Key.Equals(data.TerminalName, StringComparison.OrdinalIgnoreCase));
            if (match.Key != null)
                cmbCaptureTerminal.SelectedItem = match;
            else
                cmbCaptureTerminal.Text = data.TerminalName;
        }

        // Set mode
        SelectComboByContent(cmbCaptureMode, data.Mode);

        // Bind commodities
        dgCapturedCommodities.ItemsSource = data.Commodities;
    }

    private void ManualCapture_Click(object sender, RoutedEventArgs e)
    {
        var png = _captureService?.CaptureScreenAsPng();
        if (png != null)
            _ = ProcessCaptureAsync(png);
        else
            txtCaptureStatus.Text = "画面キャプチャに失敗しました";
    }

    private void ClipboardCapture_Click(object sender, RoutedEventArgs e)
    {
        if (Clipboard.ContainsImage())
        {
            var bitmapSource = Clipboard.GetImage();
            if (bitmapSource != null)
            {
                using var ms = new MemoryStream();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
                encoder.Save(ms);
                _ = ProcessCaptureAsync(ms.ToArray());
                return;
            }
        }
        txtCaptureStatus.Text = "クリップボードに画像がありません。ゲーム画面で PrintScreen キーを押してから実行してください。";
    }

    private void FileCapture_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "画像ファイル (*.png;*.jpg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|すべてのファイル (*.*)|*.*",
            Title = "スクリーンショットを選択"
        };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                var bytes = File.ReadAllBytes(dlg.FileName);
                // Convert to PNG if not already
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.StreamSource = new MemoryStream(bytes);
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.EndInit();
                bi.Freeze();

                using var ms = new MemoryStream();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bi));
                encoder.Save(ms);
                _ = ProcessCaptureAsync(ms.ToArray());
            }
            catch (Exception ex)
            {
                txtCaptureStatus.Text = $"ファイル読込エラー: {ex.Message}";
            }
        }
    }

    private void HotkeyEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_captureService == null) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (chkHotkeyEnabled.IsChecked == true)
            _captureService.Register(hwnd);
        else
            _captureService.Unregister();
    }

    private async void SubmitUex_Click(object sender, RoutedEventArgs e)
    {
        if (_currentCapture == null)
        {
            txtSubmitStatus.Text = "キャプチャデータがありません";
            return;
        }
        if (string.IsNullOrEmpty(App.Config.UexApiKey))
        {
            txtSubmitStatus.Text = "UEX APIキーが未設定です。設定タブで入力してください。";
            return;
        }

        // Update terminal from combo if user changed it
        if (cmbCaptureTerminal.SelectedItem is KeyValuePair<string, int> selectedTerm)
        {
            _currentCapture.TerminalName = selectedTerm.Key;
            _currentCapture.TerminalId = selectedTerm.Value;
        }

        // Update mode from combo
        var mode = (cmbCaptureMode.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "BUY";
        _currentCapture.Mode = mode;

        var matched = _currentCapture.Commodities.Where(c => c.IsMatched && c.CommodityId > 0).ToList();
        if (matched.Count == 0)
        {
            txtSubmitStatus.Text = "マッチしたコモディティがありません";
            return;
        }

        if (_currentCapture.TerminalId <= 0)
        {
            txtSubmitStatus.Text = "ターミナルが選択されていないか、IDが不明です";
            return;
        }

        btnSubmitUex.IsEnabled = false;
        txtSubmitStatus.Text = "UEXに送信中...";

        try
        {
            var result = await _uexSubmitService!.SubmitAsync(
                App.Config.UexApiKey,
                _currentCapture,
                includeScreenshot: chkCaptureScreenshot.IsChecked == true);

            txtSubmitStatus.Text = result.Success
                ? $"送信成功: {matched.Count} 品目"
                : $"送信失敗: {result.Message}";
        }
        catch (Exception ex)
        {
            txtSubmitStatus.Text = $"送信エラー: {ex.Message}";
        }
        finally
        {
            btnSubmitUex.IsEnabled = true;
        }
    }

    private void ClearCapture_Click(object sender, RoutedEventArgs e)
    {
        _currentCapture = null;
        dgCapturedCommodities.ItemsSource = null;
        imgCapturePreview.Source = null;
        cmbCaptureTerminal.SelectedItem = null;
        cmbCaptureTerminal.Text = "";
        txtCaptureStatus.Text = "";
        txtSubmitStatus.Text = "";
        txtOcrConfidence.Text = "";
        txtOcrTiming.Text = "";
    }
}

// === Helper classes ===

public class TradeDetailRow
{
    public string Location { get; set; } = "";
    public string Price { get; set; } = "";
    public string Stock { get; set; } = "";
    public string Terminal { get; set; } = "";
    public bool IsCommodityView { get; set; }
}

public class TranslationRow : INotifyPropertyChanged
{
    private string _key = "";
    private string _english = "";
    private string _japanese = "";
    private string _source = "";
    private string _translator = "";
    private string _modifiedAt = "";
    private bool _isSelected;

    public string Key { get => _key; set { _key = value; OnPropertyChanged(); } }
    public string English { get => _english; set { _english = value; OnPropertyChanged(); } }
    public string Japanese { get => _japanese; set { _japanese = value; OnPropertyChanged(); } }
    public string Source { get => _source; set { _source = value; OnPropertyChanged(); } }
    public string Translator { get => _translator; set { _translator = value; OnPropertyChanged(); } }
    public string ModifiedAt { get => _modifiedAt; set { _modifiedAt = value; OnPropertyChanged(); } }
    public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class GlossaryRow : INotifyPropertyChanged
{
    private string _english = "";
    private string _japanese = "";
    private bool _isSelected;

    public string English { get => _english; set { _english = value; OnPropertyChanged(); } }
    public string Japanese { get => _japanese; set { _japanese = value; OnPropertyChanged(); } }
    public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class ChatBubble
{
    public string Text { get; set; } = "";
    public bool IsUser { get; set; }
    public bool IsError { get; set; }

    public System.Windows.Media.Brush Background => IsError
        ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xEB, 0xEE))
        : IsUser
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE3, 0xF2, 0xFD))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0xF5, 0xF5));

    public System.Windows.Media.Brush Foreground => IsError
        ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC6, 0x28, 0x28))
        : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x21, 0x21, 0x21));

    public System.Windows.HorizontalAlignment Alignment => IsUser
        ? System.Windows.HorizontalAlignment.Right
        : System.Windows.HorizontalAlignment.Left;

    private string? _htmlContent;
    public string HtmlContent => _htmlContent ??= BuildHtml();

    private string BuildHtml()
    {
        var bgColor = IsError ? "#FFEBEE" : "#F5F5F5";
        var fgColor = IsError ? "#C62828" : "#212121";
        var pipeline = new Markdig.MarkdownPipelineBuilder().Build();
        var bodyHtml = IsUser ? System.Net.WebUtility.HtmlEncode(Text)
            : Markdig.Markdown.ToHtml(Text, pipeline);
        return $@"<!DOCTYPE html><html><head><meta charset=""utf-8"">
<style>
body {{ font-family: 'Segoe UI','Meiryo',sans-serif; font-size: 13px; color: {fgColor};
       background: {bgColor}; margin: 4px 0; padding: 0; line-height: 1.5; word-wrap: break-word; }}
h1,h2,h3 {{ margin: 0.4em 0 0.2em; }}
h1 {{ font-size: 1.2em; }} h2 {{ font-size: 1.1em; }} h3 {{ font-size: 1em; }}
p {{ margin: 0.3em 0; }}
ul,ol {{ margin: 0.3em 0; padding-left: 1.5em; }}
li {{ margin: 0.1em 0; }}
code {{ background: #E8E8E8; padding: 1px 4px; border-radius: 3px; font-size: 12px; }}
pre {{ background: #E8E8E8; padding: 8px; border-radius: 4px; overflow-x: auto; }}
pre code {{ background: none; padding: 0; }}
table {{ border-collapse: collapse; margin: 0.3em 0; }}
th,td {{ border: 1px solid #CCC; padding: 4px 8px; font-size: 12px; }}
th {{ background: #E0E0E0; }}
hr {{ border: none; border-top: 1px solid #CCC; margin: 0.5em 0; }}
strong {{ font-weight: 600; }}
a {{ color: #1976D2; }}
</style></head><body>{bodyHtml}</body></html>";
    }
}

public class UiTextWriter : TextWriter
{
    private readonly Action<string> _write;
    private readonly StringBuilder _buffer = new();

    public UiTextWriter(Action<string> write) => _write = write;
    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        if (value == '\n')
        {
            _write(_buffer.ToString());
            _buffer.Clear();
        }
        else if (value != '\r')
        {
            _buffer.Append(value);
        }
    }

    public override void WriteLine(string? value)
    {
        _write((_buffer.Length > 0 ? _buffer.ToString() : "") + (value ?? ""));
        _buffer.Clear();
    }
}
