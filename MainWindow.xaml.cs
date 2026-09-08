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
using Microsoft.Data.Sqlite;
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

    // Hangar / Upgrade state
    private readonly HangarService _hangarService = new();
    private List<UpgradeShipRow> _upgradeShips = new();
    private List<UpgradeCcuRow> _upgradeCcus = new();
    private List<HangarCcu> _allCcus = new();
    private List<MeltPledgeRow> _meltPledges = new();
    private List<string> _unresolvedNames = new();     // Ship Matrix で解決できなかった船名 (警告表示用)
    private List<StoreSku> _storeSkus = new();         // RSI ストアの販売中 SKU (store_skus)
    private List<StoreRow> _storeRows = new();         // 販売中の船グリッドの全行 (フィルタ前)
    private bool _hangarProgressHooked;                // _hangarService.OnProgress を Log へ接続済み
    private bool _upgradeDataLoadedOnce;               // LoadUpgradeData が一度でも正常終了した (起動時に一度も呼ばれない経路を塞ぐ)
    private List<HangarShipInstance> _hangarShips = new();   // 同期済み保有船 (GetShipInstances の結果)
    private EquipmentService? _hangarEquip;            // ツールチップの装備表示用 (gamedata_cache.db が無ければ null)
    private bool _uexPrefetchStarted;                                // 購入先の一括取得はセッション中 1 回だけ走らせる
    private Dictionary<string, HangarPledge> _pledgeById = new(StringComparer.Ordinal);   // LoadPledges を id で引く
    private HashSet<string> _soldPledgeIds = new(StringComparer.OrdinalIgnoreCase);   // Sell (melt 予定) にした pledge。pledge 単位 (R-02)。CCU 側の pledge id 比較 (OrdinalIgnoreCase) と揃える
    private HashSet<string> _expandedPledgeIds = new(StringComparer.Ordinal);  // 保有船グリッドで展開中のグループ (pledge)

    // 装備サブタブ (所持コンポーネント / 現在の装備)
    private List<MyComponent> _myComponents = new();                 // my_components の写し
    private List<MyComponentRow> _myComponentRows = new();           // dgMyComponents の表示行 (Usable の変更を DB へ書き戻す)
    private List<LoadoutShipChoice> _extraLoadoutShips = new();      // 所持船側から開いた "my|{id}" キーの船 (GetShipInstances に無いもの)
    private bool _suppressLoadoutEvents;                             // cmbLoadoutShip の ItemsSource 再設定中は SelectionChanged を無視
    private bool _suppressAddShipNameEvents;                         // 船名欄をプログラムから設定する間は候補の絞り込み・メーカー自動入力を止める
    private bool _compTypeComboPopulated;                            // cmbCompType は 1 度だけ埋める

    // 保有船グリッド「マーク」列の選択肢 (UpgradeShipRow.MarkDisplay と対応)
    public static string[] MarkChoices { get; } = ["", "要る", "要らない", "保留"];

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
        chkEquipClassMarks.IsChecked = App.Config.EquipmentClassMarks;
        chkMissionRepMarks.IsChecked = App.Config.MissionReputationMarks;
        chkBlueprintMarks.IsChecked = App.Config.BlueprintMissionMarks;
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
        _ = RunBackgroundFetchesAsync();

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

                    // 派閥・貢献度のデータが未取得なら、この反映のタイミングで取り込む (starbreaker の 3 クエリだけ。全件インデックス構築ではない)
                    if (App.Config.MissionReputationMarks && !HasMissionReputationData())
                    {
                        SetProgress(65, "派閥・貢献度データを取得中...");
                        try
                        {
                            using var repExtractor = new GameDataExtractor(WorkDir);
                            await repExtractor.RebuildMissionReputationAsync(new Progress<string>(Log));
                        }
                        catch (Exception ex) { Log($"[Mission] 派閥・貢献度の取得に失敗: {ex.Message}"); }
                    }

                    var merged = IniMerger.Merge(english, japanese, TranslatedPath,
                        App.Config.ForceEnglishPatterns, DbPath, glossary,
                        Path.Combine(WorkDir, "gamedata_cache.db"), App.Config.MissionReputationMarks,
                        App.Config.EquipmentClassMarks, App.Config.BlueprintMissionMarks);
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

    // gamedata_cache.db に mission_reputation の行があるか (無ければ反映時に取り込む)
    // 派閥・貢献度のデータが使える状態か。
    // 行が無いとき、および取り込んだあとにゲームがパッチされた (Data.p4k の更新日時が変わった) ときは false を返し、
    // 反映のタイミングで取り込み直させる (パッチのたびに手動で再抽出しなくても最新になる)
    private bool HasMissionReputationData()
    {
        try
        {
            var path = Path.Combine(WorkDir, "gamedata_cache.db");
            if (!File.Exists(path)) return false;
            using var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            db.Open();

            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM mission_reputation";
                if (Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) <= 0) return false;
            }

            // 取り込み時の Data.p4k と今の Data.p4k を比べる。分からないときは取り込み済みとして扱う
            string? storedP4k;
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT value FROM gamedata_meta WHERE key = 'mission_reputation_p4k'";
                storedP4k = cmd.ExecuteScalar() as string;
            }
            if (string.IsNullOrEmpty(storedP4k)) return false;

            var gamePath = App.Config.GamePath;
            if (string.IsNullOrEmpty(gamePath)) return true;
            var p4k = new[]
            {
                Path.Combine(gamePath, "Data.p4k"),
                Path.Combine(gamePath, "data", "Data.p4k"),
                Path.Combine(Path.GetDirectoryName(gamePath) ?? "", "Data.p4k"),
            }.FirstOrDefault(File.Exists);
            if (p4k == null) return true;

            return File.GetLastWriteTimeUtc(p4k).ToString("o") == storedP4k;
        }
        catch { return false; }
    }

    // ゲーム内テキストに付ける印のオン／オフ。次の [3. 反映] から反映される
    private void InGameMarks_Changed(object sender, RoutedEventArgs e)
    {
        if (chkEquipClassMarks == null || chkMissionRepMarks == null || chkBlueprintMarks == null) return;   // XAML 読込中
        App.Config.EquipmentClassMarks = chkEquipClassMarks.IsChecked == true;
        App.Config.MissionReputationMarks = chkMissionRepMarks.IsChecked == true;
        App.Config.BlueprintMissionMarks = chkBlueprintMarks.IsChecked == true;
        SaveConfigToFile();
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
        var prevWorkDir = App.Config.WorkingDirectory;
        App.Config.GamePath = txtSettingsGamePath.Text.Trim();
        App.Config.WorkingDirectory = txtWorkDir.Text.Trim();
        // 作業ディレクトリが変わったら、前のディレクトリの gamedata_cache.db を掴んだままの装備サービスを捨てる
        // (次に必要になったときに新しいディレクトリで作り直され、購入先の一括取得もやり直される)
        if (!string.Equals(prevWorkDir, App.Config.WorkingDirectory, StringComparison.OrdinalIgnoreCase))
        {
            _hangarEquip?.Dispose();
            _hangarEquip = null;
            _uexPrefetchStarted = false;
        }
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
                App.Config.EquipmentClassMarks,
                App.Config.BlueprintMissionMarks,
                App.Config.MissionReputationMarks,
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
    private bool _missionUiInitializing;

    private void LoadMissions_Click(object sender, RoutedEventArgs e) => LoadMissionsAsync();

    private async void LoadMissionsAsync()
    {
        try
        {
            var dbPath = IndexDbPath;
            if (!File.Exists(dbPath))
            {
                Dispatcher.Invoke(() => txtMissionStatus.Text = "インデックス未構築");
                return;
            }

            Dispatcher.Invoke(() =>
            {
                btnLoadMissions.IsEnabled = false;
                txtMissionStatus.Text = "ミッション読み込み中...";
            });

            var transDbPath = DbPath;
            var svc = await Task.Run(() =>
            {
                try { return new MissionService(dbPath, transDbPath); }
                catch { return null; }
            });
            if (svc == null)
            {
                Dispatcher.Invoke(() => txtMissionStatus.Text = "ミッションDB読み込み失敗");
                return;
            }

            var categories = await Task.Run(() => svc.GetCategories());
            var factions = await Task.Run(() => svc.GetFactions());
            var allMissions = await Task.Run(() => svc.Search(""));

            Dispatcher.Invoke(() =>
            {
                _missionService?.Dispose();
                _missionService = svc;
                _missionUiInitializing = true;
                lstMissionCategories.ItemsSource = categories;
                cmbMissionFaction.ItemsSource = factions;
                cmbMissionFaction.SelectedIndex = 0;
                cmbMissionRank.ItemsSource = _missionService.GetRanks();
                cmbMissionRank.SelectedIndex = 0;
                _missionUiInitializing = false;

                var transInfo = _missionService.TransLoadError != null
                    ? $"翻訳DBエラー:{_missionService.TransLoadError}"
                    : $"翻訳DB:{_missionService.TransDictCount}";
                txtMissionStatus.Text = $"{categories.Sum(c => c.Count)} 件 ({transInfo})";
                _currentMissions = allMissions;
                dgMissions.ItemsSource = _currentMissions;
                txtMissionDetail.Text = "ミッションを選択すると詳細が表示されます。";
                btnLoadMissions.IsEnabled = true;
            });
        }
        catch (Exception ex)
        {
            try
            {
                var logPath = Path.Combine(WorkDir, "mission_load_error.log");
                File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n");
                Dispatcher.Invoke(() => { txtMissionStatus.Text = $"読み込みエラー: {ex.Message}"; btnLoadMissions.IsEnabled = true; });
            }
            catch { }
        }
    }

    private async void MissionCategory_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_missionService == null) return;
        if (lstMissionCategories.SelectedItem is not MissionService.MissionCategory cat) return;

        try
        {
            txtMissionSearch.Text = "";
            txtMissionSearchStatus.Text = "";
            var svc = _missionService;
            _currentMissions = await Task.Run(() => svc.GetMissions(cat.Name));
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

    private async void MissionFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_missionService == null) return;
        if (_missionUiInitializing) return;
        var faction = cmbMissionFaction.SelectedItem as string;
        var rank = cmbMissionRank.SelectedItem as string;
        if (faction == "(すべて)" && rank == "(すべて)") return;

        txtMissionSearchStatus.Text = "検索中...";
        try
        {
            lstMissionCategories.SelectedIndex = -1;
            txtMissionSearch.Text = "";
            var svc = _missionService;
            var filtered = await Task.Run(() =>
            {
                var all = svc.Search("");
                return svc.FilterByFactionAndRank(all, faction, rank);
            });
            _currentMissions = filtered;
            dgMissions.ItemsSource = _currentMissions;
            txtMissionSearchStatus.Text = $"{_currentMissions.Count} 件";
            txtMissionDetail.Text = "ミッションを選択すると詳細が表示されます。";
        }
        catch (Exception ex) { txtMissionSearchStatus.Text = $"エラー: {ex.Message}"; }
    }

    private void MissionSearch_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) _ = MissionSearch_Execute();
    }
    private void MissionSearch_Click(object sender, RoutedEventArgs e) => _ = MissionSearch_Execute();
    private void MissionSearchClear_Click(object sender, RoutedEventArgs e) => _ = MissionSearchClear_Execute();

    private async Task MissionSearchClear_Execute()
    {
        txtMissionSearch.Text = "";
        txtMissionSearchStatus.Text = "";
        if (lstMissionCategories.SelectedItem is MissionService.MissionCategory cat)
        {
            var svc = _missionService;
            _currentMissions = svc == null ? null : await Task.Run(() => svc.GetMissions(cat.Name));
            dgMissions.ItemsSource = _currentMissions;
        }
        else
        {
            dgMissions.ItemsSource = null;
        }
    }

    private async Task MissionSearch_Execute()
    {
        if (_missionService == null)
        {
            txtMissionSearchStatus.Text = "先にミッション読み込みを実行してください";
            return;
        }
        var query = txtMissionSearch.Text.Trim();
        if (string.IsNullOrEmpty(query)) { await MissionSearchClear_Execute(); return; }

        txtMissionSearchStatus.Text = "検索中...";
        try
        {
            lstMissionCategories.SelectedIndex = -1;
            var svc = _missionService;
            var faction = cmbMissionFaction.SelectedItem as string;
            var rank = cmbMissionRank.SelectedItem as string;
            _currentMissions = await Task.Run(() =>
            {
                var found = svc.Search(query);
                return svc.FilterByFactionAndRank(found, faction, rank);
            });
            dgMissions.ItemsSource = _currentMissions;
            txtMissionSearchStatus.Text = $"{_currentMissions.Count} 件";

            if (_currentMissions.Count == 0)
            {
                var transHits = await Task.Run(() => svc.SearchTranslations(query));
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
        LoadMissionsAsync();

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

        try
        {
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
        catch (Exception ex)
        {
            txtGameDataStatus.Text = $"DB初期化エラー: {ex.Message}";
            try { File.AppendAllText(Path.Combine(WorkDir, "startup_error.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] InitGameData: {ex}\n"); } catch { }
        }
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

    // 船名のツールチップは開く直前に生成する (行の実体化時に船ごとの SQL を前倒しで走らせない)
    private void ShipTooltip_Opening(object sender, ToolTipEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        var text = fe.DataContext switch
        {
            UpgradeShipRow r => r.Tooltip,
            MyShipRow m => m.Tooltip,
            _ => "",
        };
        if (string.IsNullOrWhiteSpace(text)) { e.Handled = true; return; }
        fe.ToolTip = text;
    }

    // 装備アイテムのツールチップ。1 行目にアイテム名、2 行目に record 名、そのあとに購入先 (UEX のローカルキャッシュ)。
    // ネットワークは触らない (取得は起動時のバックグラウンド一括取得のみ)
    private string BuildItemTooltip(string itemName, string itemRecord)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(itemName)) parts.Add(itemName);
        if (!string.IsNullOrWhiteSpace(itemRecord)) parts.Add(itemRecord);
        var purchase = _hangarEquip != null && (!string.IsNullOrWhiteSpace(itemName) || !string.IsNullOrWhiteSpace(itemRecord))
            ? _hangarEquip.GetPurchaseLocationText(itemName, itemRecord)
            : "";
        if (purchase.Length > 0) parts.Add("\n" + purchase);
        return string.Join("\n", parts);
    }

    private static void ApplyTooltip(object sender, ToolTipEventArgs e, string text)
    {
        if (sender is not FrameworkElement fe) return;
        if (string.IsNullOrWhiteSpace(text)) { e.Handled = true; return; }
        fe.ToolTip = text;
    }

    // dgLoadout「デフォルト」列
    private void LoadoutDefaultTooltip_Opening(object sender, ToolTipEventArgs e)
    {
        var row = (sender as FrameworkElement)?.DataContext as LoadoutRow;
        ApplyTooltip(sender, e, row == null ? "" : BuildItemTooltip(row.DefaultItemName, row.DefaultItemRecord));
    }

    // dgLoadout「現在」列 (未設定ポートは初期装備が出ているので、その購入先を出す)
    private void LoadoutCurrentTooltip_Opening(object sender, ToolTipEventArgs e)
    {
        var row = (sender as FrameworkElement)?.DataContext as LoadoutRow;
        ApplyTooltip(sender, e, row == null ? "" : BuildItemTooltip(row.EffectiveItemName, row.EffectiveItemRecord));
    }

    // dgMyComponents「名称」列
    private void ComponentTooltip_Opening(object sender, ToolTipEventArgs e)
    {
        var row = (sender as FrameworkElement)?.DataContext as MyComponentRow;
        ApplyTooltip(sender, e, row == null ? "" : BuildItemTooltip(row.ItemName, row.ItemRecord));
    }

    // 「装備を設定」ComboBox の各項目
    private void LoadoutChoiceTooltip_Opening(object sender, ToolTipEventArgs e)
    {
        var choice = (sender as FrameworkElement)?.DataContext as LoadoutChoice;
        ApplyTooltip(sender, e, choice == null ? "" : BuildItemTooltip(choice.ItemName, choice.ItemRecord));
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _captureService?.Dispose();
        _hangarEquip?.Dispose();

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

    private void HangarSync_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var win = new HangarSyncWindow(WorkDir) { Owner = this };
            win.ShowDialog();
            _hangarService.InvalidateCaches();
            LoadUpgradeData();

            // 同期した保有船を所持船にも反映する (既存の所持船行は変更・削除しない)。
            // ImportHangarShipsIntoMyShips は直前の LoadUpgradeData が更新した _hangarShips に依存するので順序は変えず、
            // 二重読込を避けるため中の RefreshMyShips では LoadUpgradeData を再実行しない (reloadUpgrade: false)
            var added = ImportHangarShipsIntoMyShips(confirm: false, reloadUpgrade: false);
            if (added > 0)
            {
                Log($"[Ship] 同期に伴い所持船へ {added} 件追加");
                txtMyShipStatus.Text = $"所持船: {_tradeService.MyShips.Count} 隻 | 同期で {added} 件追加";
            }
            else
            {
                Log("[Ship] 同期: 所持船は最新です");
                txtMyShipStatus.Text = $"所持船: {_tradeService.MyShips.Count} 隻 | 所持船は最新です";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"同期ウィンドウを開けませんでした: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpgradeReload_Click(object sender, RoutedEventArgs e) => LoadUpgradeData();

    private void UpgradeReset_Click(object sender, RoutedEventArgs e)
    {
        foreach (var s in _upgradeShips) s.AppliedCcuIds.Clear();
        _soldPledgeIds.Clear();
        RecomputeUpgradeSim();
    }

    // 戻り値: 正常終了なら true、途中で例外に入った (RecomputeUpgradeSim → RefreshMyShipRows に達しなかった可能性がある) なら false
    private bool LoadUpgradeData()
    {
        try
        {
            _hangarService.SetCacheDir(WorkDir);
            var ships = _hangarService.GetShipInstances();
            _hangarShips = ships;
            _allCcus = _hangarService.LoadCcus();
            _storeSkus = _hangarService.LoadStoreSkus();
            PopulateStoreCategoryCombo();

            // 装備表示用 (1 度だけ作る。gamedata_cache.db が無い/開けない場合は null のまま)
            if (_hangarEquip == null)
            {
                var gamedataPath = Path.Combine(WorkDir, "gamedata_cache.db");
                if (File.Exists(gamedataPath))
                {
                    try { _hangarEquip = new EquipmentService(gamedataPath); }
                    catch (Exception ex) { Log($"[Hangar] 装備DBを開けませんでした: {ex.Message}"); }
                }
            }

            // 購入先 (UEX) をバックグラウンドで一括取得しておく (ツールチップはローカルキャッシュだけを同期で読む)
            if (_hangarEquip != null && !_uexPrefetchStarted)
            {
                _uexPrefetchStarted = true;
                var equip = _hangarEquip;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var n = await equip.PrefetchPurchaseLocationsAsync();
                        Dispatcher.Invoke(() => Log($"[Equip] 購入先 (UEX) を取得: {n} 件"));
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.Invoke(() => Log($"[Equip] 購入先 (UEX) の取得に失敗: {ex.Message}"));
                    }
                });
            }

            var pledges = _hangarService.LoadPledges();
            _pledgeById = pledges.Where(p => !string.IsNullOrEmpty(p.Id))
                .GroupBy(p => p.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            // 再読込前のシミュレーション状態 (適用中の CCU) を InstanceKey で保存し、再生成後の同キーの船行へ復元する
            // (_soldPledgeIds / _expandedPledgeIds は従来どおりフィールドで保持)
            var prevApplied = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var r in _upgradeShips.Where(r => !r.IsGroupHeader && !r.IsCcu && r.AppliedCcuIds.Count > 0))
                if (!string.IsNullOrEmpty(r.InstanceKey)) prevApplied.TryAdd(r.InstanceKey, r.AppliedCcuIds.ToList());

            // 再読込前の選択行も記録する (行は作り直されるのでキーで持つ)。船行 = InstanceKey、CCU 行 = (CCU の pledge id, 親船行の InstanceKey)。
            // RecomputeUpgradeSim → RefreshUpgradeShipGrid の後に一致する行を選択し直す
            var prevSelected = dgUpgradeShips.SelectedItem as UpgradeShipRow;
            var prevSelShipKey = prevSelected != null && !prevSelected.IsGroupHeader && !prevSelected.IsCcu ? prevSelected.InstanceKey : "";
            var prevSelCcuId = prevSelected != null && prevSelected.IsCcu ? prevSelected.CcuPledgeId : "";
            var prevSelCcuParentKey = prevSelected != null && prevSelected.IsCcu ? (prevSelected.CcuParent?.InstanceKey ?? "") : "";
            var prevSelHeaderKey = prevSelected != null && prevSelected.IsGroupHeader ? prevSelected.GroupPledgeId : "";

            UpgradeShipRow MakeShipRow(HangarShipInstance s)
            {
                _pledgeById.TryGetValue(s.PledgeId, out var p);
                var meltable = p?.Meltable ?? true;
                return new UpgradeShipRow
                {
                    PledgeId = s.PledgeId,
                    PledgeName = s.PledgeName,
                    Name = s.Name,
                    InstanceKey = s.InstanceKey,
                    AppliedCcuIds = prevApplied.TryGetValue(s.InstanceKey, out var applied) ? new List<string>(applied) : new List<string>(),
                    CurrentName = s.Name,
                    DisplayName = s.Name,
                    InsuranceDisplay = s.InsuranceDisplay,
                    Focus = s.Matrix?.Focus ?? "",
                    Type = s.Matrix?.Type ?? "",
                    Status = s.Matrix?.ProductionStatus ?? "",
                    IsUnresolved = s.IsUnresolved,
                    Mark = s.Mark,
                    SameTypeCount = s.SameTypeCount,
                    OriginDisplay = s.OriginDisplay,
                    PledgeShipNames = s.PledgeShipNames,
                    // ツールチップは初回表示時に生成 (58 船分の SQL を UI スレッドで前倒し実行しない)
                    TooltipFactory = () => _hangarService.BuildShipTooltip(s, _hangarEquip, s.InstanceKey),
                    Meltable = meltable,
                    PledgeValueCents = p?.ValueCents ?? 0,
                    PriceDisplay = p?.ValueDisplay ?? "",
                    SellEnabled = meltable && !string.IsNullOrEmpty(s.PledgeId),
                };
            }

            // 同 pledge に船が 2 機以上ある pledge は「ヘッダ行 + 子行」にする (Packages の展開表示)。
            // 初めて現れたグループは既定で展開。既知のグループは前回の展開状態を維持する
            var prevHeaderIds = new HashSet<string>(
                _upgradeShips.Where(r => r.IsGroupHeader).Select(r => r.GroupPledgeId), StringComparer.Ordinal);
            var entries = new List<(string Key, List<UpgradeShipRow> Rows)>();
            foreach (var g in ships.GroupBy(s => s.PledgeId, StringComparer.Ordinal))
            {
                var rows = g.OrderBy(s => s.Name).Select(MakeShipRow).ToList();
                if (rows.Count >= 2 && !string.IsNullOrEmpty(g.Key))
                {
                    _pledgeById.TryGetValue(g.Key, out var p);
                    var pledgeName = p?.Name ?? rows[0].PledgeName;
                    if (string.IsNullOrEmpty(pledgeName)) pledgeName = "(pledge)";
                    var pledgeShipNames = p != null ? string.Join(", ", p.ShipNames) : rows[0].PledgeShipNames;
                    var meltable = p?.Meltable ?? true;
                    var header = new UpgradeShipRow
                    {
                        IsGroupHeader = true,
                        GroupPledgeId = g.Key,
                        PledgeId = g.Key,
                        PledgeName = pledgeName,
                        Name = pledgeName,
                        CurrentName = pledgeName,
                        DisplayName = $"{pledgeName} ({rows.Count}機)",
                        InsuranceDisplay = p?.InsuranceDisplay ?? rows[0].InsuranceDisplay,
                        OriginDisplay = rows[0].OriginDisplay,
                        PledgeShipNames = pledgeShipNames,
                        Meltable = meltable,
                        PledgeValueCents = p?.ValueCents ?? 0,
                        PriceDisplay = p?.ValueDisplay ?? "",
                        SellEnabled = meltable,
                        Tooltip = $"{pledgeName}\n同梱: {pledgeShipNames}\nmelt 額: {(p?.ValueDisplay ?? "不明")}",
                    };
                    foreach (var c in rows)
                    {
                        c.IsChild = true;
                        c.GroupPledgeId = g.Key;
                        c.DisplayName = "　└ " + c.Name;
                        c.PriceDisplay = "";   // pledge 額はヘッダ行に出す
                    }
                    if (!prevHeaderIds.Contains(g.Key)) _expandedPledgeIds.Add(g.Key);
                    rows.Insert(0, header);
                    entries.Add((pledgeName, rows));
                }
                else
                {
                    foreach (var r in rows) entries.Add((r.Name, new List<UpgradeShipRow> { r }));
                }
            }
            // Hangar (pledge) に一致しない所持船 (my_ships) も保有船として行にする (ゲーム内購入 / 手入力)。
            // 正規化名が GetShipInstances のどれとも一致しないものだけ。CCU 適用・Sell は不可、装備は設定可
            var hangarNorms = new HashSet<string>(
                ships.Select(s => _hangarService.NormalizeShipName(s.Name)), StringComparer.OrdinalIgnoreCase);
            foreach (var my in _tradeService.MyShips)
            {
                if (hangarNorms.Contains(_hangarService.NormalizeShipName(my.Name))) continue;
                var inGame = IsInGameNotes(my.Notes);
                var myId = my.Id;
                var myName = my.Name;
                var matrix = _hangarService.ResolveShip(myName);
                var row = new UpgradeShipRow
                {
                    PledgeId = "",
                    PledgeName = "",
                    Name = myName,
                    InstanceKey = $"my|{myId}",
                    CurrentName = myName,
                    DisplayName = myName,
                    InsuranceDisplay = "",
                    Focus = matrix?.Focus ?? "",
                    Type = matrix?.Type ?? "",
                    Status = matrix?.ProductionStatus ?? "",
                    IsUnresolved = matrix == null,
                    OriginDisplay = inGame ? "ゲーム内購入" : "手入力 (未同期)",
                    IsInGame = inGame,
                    IsManual = !inGame,
                    MyShipId = myId,
                    CanUpgrade = false,
                    SellEnabled = false,
                    // 未同期の所持船と同じツールチップ経路 (Ship Matrix の情報 + "my|{id}" の装備)
                    TooltipFactory = () =>
                    {
                        var tmp = new HangarShipInstance { Name = myName, Matrix = matrix };
                        return _hangarService.BuildShipTooltip(tmp, _hangarEquip, $"my|{myId}");
                    },
                };
                entries.Add((row.Name, new List<UpgradeShipRow> { row }));
            }
            _upgradeShips = entries.OrderBy(e => e.Key).SelectMany(e => e.Rows).ToList();

            // melt シミュレーション用の pledge 一覧 (Selected は _soldPledgeIds と双方向に同期)
            _meltPledges = pledges.Select(p => new MeltPledgeRow
            {
                PledgeId = p.Id,
                Name = p.Name,
                ValueDisplay = p.ValueDisplay,
                InsuranceDisplay = p.InsuranceDisplay,
                Ships = string.Join(", ", p.ShipNames),
                Meltable = p.Meltable,
                CanSelect = p.Meltable,   // 適用中 CCU の判定は RecomputeUpgradeSim で更新
                Selected = !string.IsNullOrEmpty(p.Id) && _soldPledgeIds.Contains(p.Id),
            }).ToList();
            foreach (var m in _meltPledges) m.PropertyChanged += MeltPledgeRow_PropertyChanged;
            dgMeltPledges.ItemsSource = null;
            dgMeltPledges.ItemsSource = _meltPledges;

            var totals = _hangarService.GetTotals();
            txtBuybackTokens.Text = totals.BuybackTokens.HasValue
                ? $"Buy Back 残トークン: {totals.BuybackTokens.Value}"
                : "Buy Back 残トークン: 未取得";

            // Ship Matrix で解決できない船名は無言で捨てず、ステータス行で警告する
            // (RecomputeUpgradeSim が txtUpgradeStatus を組み立てる際に ApplyUnresolvedWarning で付加する)
            _unresolvedNames = _hangarService.GetUnresolvedNames();
            // 所持船由来の行 (IsNonPledge) で Ship Matrix に解決できない船名も加える (重複排除)
            {
                var seen = new HashSet<string>(_unresolvedNames, StringComparer.OrdinalIgnoreCase);
                foreach (var s in _upgradeShips.Where(s => s.IsNonPledge))
                {
                    var n = (s.Name ?? "").Trim();
                    if (n.Length == 0) continue;
                    if (_hangarService.ResolveShip(n) != null) continue;
                    if (seen.Add(n)) _unresolvedNames.Add(n);
                }
            }

            RecomputeUpgradeSim();

            // 選択の復元 (再生成後の行から同じキーの行を探す)
            if (dgUpgradeShips.ItemsSource is List<UpgradeShipRow> shown)
            {
                UpgradeShipRow? reselect = null;
                if (!string.IsNullOrEmpty(prevSelShipKey))
                    reselect = shown.FirstOrDefault(r => !r.IsGroupHeader && !r.IsCcu && r.InstanceKey.Equals(prevSelShipKey, StringComparison.Ordinal));
                else if (!string.IsNullOrEmpty(prevSelCcuId))
                    reselect = shown.FirstOrDefault(r => r.IsCcu
                        && r.CcuPledgeId.Equals(prevSelCcuId, StringComparison.OrdinalIgnoreCase)
                        && (r.CcuParent?.InstanceKey ?? "").Equals(prevSelCcuParentKey, StringComparison.Ordinal));
                else if (!string.IsNullOrEmpty(prevSelHeaderKey))
                    reselect = shown.FirstOrDefault(r => r.IsGroupHeader && r.GroupPledgeId.Equals(prevSelHeaderKey, StringComparison.Ordinal));
                if (reselect != null)
                {
                    dgUpgradeShips.SelectedItem = reselect;
                    dgUpgradeShips.ScrollIntoView(reselect);
                }
            }

            // 装備サブタブ (船セレクタ・所持コンポーネント・現在の装備グリッド) も同期状態に合わせる
            RefreshMyComponents();
            RefreshLoadoutShipCombo();
            _upgradeDataLoadedOnce = true;
            return true;
        }
        catch (Exception ex)
        {
            txtUpgradeStatus.Text = $"読み込みエラー: {ex.Message}";
            return false;
        }
    }

    // txtUpgradeStatus に未解決船名の警告を付ける (0 件なら ToolTip を外す)。
    // RecomputeUpgradeSim が Text を組み立て直した直後に呼ぶ
    private void ApplyUnresolvedWarning()
    {
        if (_unresolvedNames.Count > 0)
        {
            txtUpgradeStatus.Text += $" | ⚠ 未解決の船名 {_unresolvedNames.Count} 件（ツールチップ参照）";
            txtUpgradeStatus.ToolTip = string.Join("\n", _unresolvedNames);
        }
        else
        {
            txtUpgradeStatus.ToolTip = null;
        }
    }

    // 保有船グリッド末尾「使用不可のアップグレード権利」グループの GroupPledgeId (展開状態は _expandedPledgeIds で管理)
    private const string UnusableCcuGroupId = "__unusable__";
    // 「使用不可のアップグレード権利」ヘッダ行は再描画のたびに作り直さず同一インスタンスを使う (選択が外れないように)
    private UpgradeShipRow? _unusableHeaderRow;

    // 保有船グリッドの表示行: ヘッダ行・単独行は常に、子行は展開中のグループのみ (_upgradeShips 自体は全行保持)。
    // 各船行 (子行/単独行) の直後に、その船に適用済みの CCU (AppliedCcuIds 順) と現在適用可能な未使用 CCU を
    // 「アップグレード」行として差し込み、どの船にも適用できない CCU は末尾の「使用不可のアップグレード権利」グループ
    // (既定は折りたたみ) の子として出す。CCU 行は _upgradeShips には入れず、射影のたびに作り直す
    private void RefreshUpgradeShipGrid()
    {
        var selected = dgUpgradeShips.SelectedItem as UpgradeShipRow;
        var ccuById = _allCcus.Where(c => !string.IsNullOrEmpty(c.PledgeId))
            .GroupBy(c => c.PledgeId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var stateById = _upgradeCcus.Where(r => !string.IsNullOrEmpty(r.PledgeId))
            .GroupBy(r => r.PledgeId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var usable = _upgradeCcus.Where(r => r.StateDisplay == "使用可").ToList();
        // 売却予定 (Sell) にした CCU。使用不可扱いだが「使用不可のアップグレード権利」グループではなく元船の下 (使用可の後) に出す
        var soldCcus = _upgradeCcus.Where(r => r.StateDisplay == "売却予定").ToList();
        var placedSoldCcuIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 現在保有型 (正規化) ごとの船数 (売却予定の船は除く)。同じ未使用 CCU が同型船 N 機の下に重複表示されるとき
        // 「(他 N-1 機でも使用可)」を CCU 行の表示名に付けるために使う
        var heldCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _upgradeShips.Where(s => !s.IsGroupHeader && !s.IsSold && !s.IsNonPledge))
        {
            var n = _hangarService.NormalizeShipName(s.CurrentName);
            heldCounts[n] = heldCounts.TryGetValue(n, out var cnt) ? cnt + 1 : 1;
        }

        // 帰属決定: 表示の有無 (グループの展開状態) に関係なく、全船行 (ヘッダ行を除く。売却予定の船を含む) を基準に
        // 「その CCU がどの船の下に置かれるか」を先に確定する (FromShip 正規化 == CurrentName 正規化)。
        //   使用可 CCU  … 売却予定でない船の下 (同型船が複数あれば各船の下に重複表示)
        //   売却予定 CCU … 元船の下 (船自体が売却予定でも Unsell できるよう出す)。ここで placedSoldCcuIds も確定する
        // 折りたたみ中の子行に属する CCU は、後段の射影で子行と一緒に非表示になる (「使用不可のアップグレード権利」グループには出さない)
        var usableByShip = new Dictionary<UpgradeShipRow, List<UpgradeCcuRow>>(ReferenceEqualityComparer.Instance);
        var soldByShip = new Dictionary<UpgradeShipRow, List<UpgradeCcuRow>>(ReferenceEqualityComparer.Instance);
        foreach (var s in _upgradeShips)
        {
            if (s.IsGroupHeader) continue;
            if (s.IsNonPledge) continue;   // Hangar 未一致の所持船 (ゲーム内購入 / 手入力) には CCU を帰属させない
            var current = _hangarService.NormalizeShipName(s.CurrentName);
            bool FromMatches(UpgradeCcuRow r) =>
                ccuById.TryGetValue(r.PledgeId, out var c)
                && _hangarService.NormalizeShipName(c.FromShip).Equals(current, StringComparison.OrdinalIgnoreCase);

            if (!s.IsSold) usableByShip[s] = usable.Where(FromMatches).ToList();
            var sold = soldCcus.Where(FromMatches).ToList();
            soldByShip[s] = sold;
            foreach (var r in sold) placedSoldCcuIds.Add(r.PledgeId);
        }

        // 射影: ヘッダ行・単独行は常に、子行は展開中のグループのみ。各船行の直後に帰属決定済みの CCU 行を差し込む
        var list = new List<UpgradeShipRow>();
        foreach (var s in _upgradeShips)
        {
            if (s.IsChild && !_expandedPledgeIds.Contains(s.GroupPledgeId)) continue;
            list.Add(s);
            if (s.IsGroupHeader) continue;

            var indent = s.IsChild ? "　　↳ " : "　↳ ";

            // この船に適用済みの CCU (適用順)。Undo は末尾の 1 本のみ可 (途中を外すと連鎖が壊れる)
            for (var i = 0; i < s.AppliedCcuIds.Count; i++)
            {
                if (!ccuById.TryGetValue(s.AppliedCcuIds[i], out var c)) continue;
                stateById.TryGetValue(c.PledgeId, out var st);
                list.Add(MakeCcuRow(c, st, s, indent, "使用中", isLastApplied: i == s.AppliedCcuIds.Count - 1));
            }

            var current = _hangarService.NormalizeShipName(s.CurrentName);

            // この船に現在適用可能な未使用 CCU。売却予定の船には出さない (usableByShip に無い)。
            // 表示順は 使用中 → 使用可 → 売却予定
            if (usableByShip.TryGetValue(s, out var usableHere))
            {
                var others = heldCounts.TryGetValue(current, out var hc) ? hc - 1 : 0;   // 同型の他の船 (同じ CCU が使える) の数
                foreach (var r in usableHere)
                {
                    if (!ccuById.TryGetValue(r.PledgeId, out var c)) continue;
                    var row = MakeCcuRow(c, r, s, indent, "使用可", isLastApplied: false);
                    if (others >= 1) row.DisplayName += $" (他 {others} 機でも使用可)";
                    list.Add(row);
                }
            }

            // 売却予定にした CCU のうち元船がこの船のもの
            if (soldByShip.TryGetValue(s, out var soldHere))
            {
                foreach (var r in soldHere)
                {
                    if (!ccuById.TryGetValue(r.PledgeId, out var c)) continue;
                    list.Add(MakeCcuRow(c, r, s, indent, "売却予定", isLastApplied: false));
                }
            }
        }

        // どの船にも適用できない CCU (使用不可 / 使用不可 (売却))。
        // 売却予定 CCU は元船の下に出すので除外する (元船を保有しておらず置き場が無かったものだけはここに出す)。
        // placedSoldCcuIds は帰属決定で全船行を基準に確定済みなので、元船が折りたたみ中でもここには出ない
        var unusable = _upgradeCcus.Where(r => r.IsUnusable
            && (r.StateDisplay != "売却予定" || !placedSoldCcuIds.Contains(r.PledgeId))).ToList();
        if (unusable.Count > 0)
        {
            var expanded = _expandedPledgeIds.Contains(UnusableCcuGroupId);
            _unusableHeaderRow ??= new UpgradeShipRow
            {
                IsGroupHeader = true,
                GroupPledgeId = UnusableCcuGroupId,
                Name = "使用不可のアップグレード権利",
                CurrentName = "使用不可のアップグレード権利",
                Tooltip = "保有船のどれにも適用できないアップグレード権利 (元船を保有していない、または元船を売却予定にした)",
                SellEnabled = false,
            };
            _unusableHeaderRow.IsExpanded = expanded;
            _unusableHeaderRow.DisplayName = $"使用不可のアップグレード権利 ({unusable.Count})";
            list.Add(_unusableHeaderRow);
            if (expanded)
            {
                foreach (var r in unusable)
                {
                    if (!ccuById.TryGetValue(r.PledgeId, out var c)) continue;
                    list.Add(MakeCcuRow(c, r, null, "　↳ ", r.StateDisplay, isLastApplied: false));
                }
            }
        }

        dgUpgradeShips.ItemsSource = null;
        dgUpgradeShips.ItemsSource = list;

        // 選択の復元: 船行・ヘッダ行は同一インスタンスが表示リストに残っている場合のみ (参照一致で確認)。
        // LoadUpgradeData から呼ばれた際は _upgradeShips が作り直されていて旧オブジェクトは表示リストに無いので代入しない (使用不可グループのヘッダ行は同一インスタンスを再利用するため例外)
        // (その場合の復元は LoadUpgradeData 側がキーで行う)。CCU 行は作り直されるので同じ CCU・同じ親の行を探す
        if (selected == null) return;
        var reselect = !selected.IsCcu
            ? (list.Any(r => ReferenceEquals(r, selected)) ? selected : null)
            : list.FirstOrDefault(r => r.IsCcu
                && r.CcuPledgeId.Equals(selected.CcuPledgeId, StringComparison.OrdinalIgnoreCase)
                && ReferenceEquals(r.CcuParent, selected.CcuParent));
        if (reselect != null) dgUpgradeShips.SelectedItem = reselect;
    }

    // 保有船グリッドに差し込む CCU (アップグレード権利) 1 行。parent は適用先／適用元の船行 (使用不可グループの子は null)
    private UpgradeShipRow MakeCcuRow(HangarCcu c, UpgradeCcuRow? state, UpgradeShipRow? parent, string indent, string ccuState, bool isLastApplied)
    {
        _pledgeById.TryGetValue(c.PledgeId, out var p);
        var meltable = p?.Meltable ?? true;
        var pledgeName = string.IsNullOrEmpty(p?.Name) ? c.RouteDisplay : p!.Name;

        var toMatrix = _hangarService.ResolveShip(c.ToShip);
        var toInfo = "";
        if (toMatrix != null)
        {
            var parts = new[] { toMatrix.Focus, toMatrix.Type }.Where(x => !string.IsNullOrEmpty(x)).ToList();
            if (parts.Count > 0) toInfo = $" ({string.Join(" / ", parts)})";
        }
        var tip = $"{pledgeName}\n支払額 {c.PriceDisplay}";
        if (c.StdPriceCents > 0) tip += $" / 標準CCU額 ${c.StdPriceCents / 100.0:N2}";
        if (!string.IsNullOrEmpty(c.DiscountDisplay)) tip += $" / 割引 {c.DiscountDisplay}";
        tip += $"\n先船: {c.ToShip}{toInfo}";

        var toSale = state?.ToShipSaleDisplay ?? "";
        return new UpgradeShipRow
        {
            IsCcu = true,
            CcuPledgeId = c.PledgeId,
            CcuState = ccuState,
            CcuParent = parent,
            CcuIsLastApplied = isLastApplied,
            PledgeId = c.PledgeId,               // Sell は pledge 単位 (UpgradeSell_Click が PledgeId を使う)
            PledgeName = p?.Name ?? "",
            Name = c.RouteDisplay,
            CurrentName = c.ToShip,
            DisplayName = $"{indent}Upgrade: {c.FromShip} → {c.ToShip}",
            GroupPledgeId = parent?.GroupPledgeId ?? UnusableCcuGroupId,
            OriginDisplay = $"アップグレード ← {c.FromShip}",   // 由来 = アップグレード + 何からのアップグレードか
            PledgeShipNames = p?.Name ?? "",     // 由来セルの ToolTip (pledge 名)
            PriceDisplay = c.PriceDisplay,
            DiscountDisplay = c.DiscountDisplay,
            WarbondDisplay = c.Warbond ? "Warbond" : "",
            ToShipSaleDisplay = toSale,
            OnSaleDisplay = toSale,              // 「販売」列 = 先船の販売状況
            OnSaleWarbond = toSale == "Warbond",
            Tooltip = tip,
            Meltable = meltable,
            PledgeValueCents = p?.ValueCents ?? 0,
            // 適用中 (使用中) の CCU は先に Undo が必要なので Sell 不可 (SellToolTip で案内)
            SellEnabled = meltable && !string.IsNullOrEmpty(c.PledgeId) && ccuState != "使用中",
            IsSold = !string.IsNullOrEmpty(c.PledgeId) && _soldPledgeIds.Contains(c.PledgeId),
        };
    }

    // ヘッダ行の「+/−」: グループの展開状態を反転する
    private void UpgradeGroupToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not UpgradeShipRow row) return;
        if (!row.IsGroupHeader || string.IsNullOrEmpty(row.GroupPledgeId)) return;
        if (!_expandedPledgeIds.Remove(row.GroupPledgeId)) _expandedPledgeIds.Add(row.GroupPledgeId);
        row.IsExpanded = _expandedPledgeIds.Contains(row.GroupPledgeId);
        RefreshUpgradeShipGrid();
    }

    // Sell / Unsell: pledge 単位で melt 予定に入れる／外す (パック内の 1 機だけは melt できない: R-02)
    private void UpgradeSell_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not UpgradeShipRow row) return;
        if (!row.SellEnabled || string.IsNullOrEmpty(row.PledgeId)) return;
        if (!_soldPledgeIds.Remove(row.PledgeId)) _soldPledgeIds.Add(row.PledgeId);
        RecomputeUpgradeSim();
    }

    // melt シミュレーションパネルのチェック → _soldPledgeIds へ反映 (RecomputeUpgradeSim 側からの同期は状態が一致するので何もしない)
    private void MeltPledgeRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MeltPledgeRow.Selected) || sender is not MeltPledgeRow row) return;
        if (string.IsNullOrEmpty(row.PledgeId)) return;
        if (row.Selected == _soldPledgeIds.Contains(row.PledgeId)) return;
        if (row.Selected) _soldPledgeIds.Add(row.PledgeId); else _soldPledgeIds.Remove(row.PledgeId);
        RecomputeUpgradeSim();
    }

    // 適用状態から各船の現在名・各権利の状態を再計算し、保有船グリッド (船行 + CCU 行) を更新する
    private void RecomputeUpgradeSim()
    {
        var ccuById = _allCcus.ToDictionary(c => c.PledgeId, StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shipRows = _upgradeShips.Where(s => !s.IsGroupHeader).ToList();   // ヘッダ行は船ではない

        foreach (var s in _upgradeShips)
        {
            s.IsSold = !string.IsNullOrEmpty(s.PledgeId) && _soldPledgeIds.Contains(s.PledgeId);
            s.IsExpanded = s.IsGroupHeader && _expandedPledgeIds.Contains(s.GroupPledgeId);
        }

        foreach (var s in shipRows)
        {
            s.CurrentName = s.Name;
            foreach (var id in s.AppliedCcuIds)
            {
                if (!ccuById.TryGetValue(id, out var c)) continue;
                s.CurrentName = c.ToShip;
                used.Add(id);
            }
        }

        // 比較は名寄せ後 (Ship Matrix の正式名) で行う。表示は元の文字列のまま。売却 (melt 予定) の船は保有から除く。
        // Hangar 未一致の所持船 (ゲーム内購入 / 手入力) は CCU の適用可否判定に使わないので除く
        var heldCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in shipRows.Where(s => !s.IsSold && !s.IsNonPledge))
        {
            var n = _hangarService.NormalizeShipName(s.CurrentName);
            heldCounts[n] = heldCounts.TryGetValue(n, out var cnt) ? cnt + 1 : 1;
        }
        var heldNames = new HashSet<string>(heldCounts.Keys, StringComparer.OrdinalIgnoreCase);

        // R-06: 売却船を元船とする CCU は使用不能 (元の型・適用後の型の両方)
        var soldTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in shipRows.Where(s => s.IsSold))
        {
            soldTypes.Add(_hangarService.NormalizeShipName(s.Name));
            soldTypes.Add(_hangarService.NormalizeShipName(s.CurrentName));
        }

        foreach (var s in shipRows)
        {
            var current = _hangarService.NormalizeShipName(s.CurrentName);
            // 未使用かつ売却予定 (Sell / melt パネル) でない CCU だけが候補 (下の _upgradeCcus の usable 判定と同じ条件)。
            // CCU は pledge の船にしか使えないので Hangar 未一致の所持船 (IsNonPledge) は常に不可
            s.CanUpgrade = !s.IsSold && !s.IsNonPledge && _allCcus.Any(c => !used.Contains(c.PledgeId)
                && !(!string.IsNullOrEmpty(c.PledgeId) && _soldPledgeIds.Contains(c.PledgeId))
                && _hangarService.NormalizeShipName(c.FromShip).Equals(current, StringComparison.OrdinalIgnoreCase));
            // Hangar 未一致の所持船 (IsNonPledge) は heldCounts に数えていないので重複判定もしない (常に false)
            s.IsDuplicate = !s.IsSold && !s.IsNonPledge && heldCounts.TryGetValue(current, out var cnt) && cnt >= 2;

            // RSI ストアでの販売状況 (保有船名で照合)
            var warbond = _hangarService.FindStoreShipWarbond(s.Name);
            var normal = _hangarService.FindStoreShip(s.Name);
            s.OnSaleWarbond = warbond != null;
            s.OnSaleDisplay = warbond != null ? "Warbond 販売中" : normal != null ? "販売中" : "";
            s.UpgradableDisplay = s.CanUpgrade ? "○" : "";
            s.StoreUpgradeDisplay = StoreUpgradeDisplayFor(s.Name);
        }

        _upgradeCcus = _allCcus.Select(c =>
        {
            var isUsed = used.Contains(c.PledgeId);
            // CCU 自体が売却予定 (Sell / melt パネル) なら、元船を保有していても使用不可扱い (heldNames 判定より前に除外)
            var isSoldCcu = !string.IsNullOrEmpty(c.PledgeId) && _soldPledgeIds.Contains(c.PledgeId);
            var from = _hangarService.NormalizeShipName(c.FromShip);
            var usable = !isUsed && !isSoldCcu && heldNames.Contains(from);
            var blockedBySell = !isUsed && !isSoldCcu && !usable && soldTypes.Contains(from);   // R-06
            return new UpgradeCcuRow
            {
                PledgeId = c.PledgeId,
                RouteDisplay = c.RouteDisplay,
                PriceDisplay = c.PriceDisplay,
                DiscountDisplay = c.DiscountDisplay,
                WarbondDisplay = c.Warbond ? "Warbond" : "",
                StateDisplay = isUsed ? "使用中" : isSoldCcu ? "売却予定" : usable ? "使用可" : blockedBySell ? "使用不可 (売却)" : "使用不可",
                IsUsed = isUsed,
                IsUnusable = !isUsed && !usable,
                IsDuplicateTarget = heldNames.Contains(_hangarService.NormalizeShipName(c.ToShip)),
                ToShipSaleDisplay = _hangarService.FindStoreShipWarbond(c.ToShip) != null ? "Warbond"
                    : _hangarService.FindStoreShip(c.ToShip) != null ? "販売中" : "",
            };
        }).OrderBy(r => r.IsUnusable).ThenBy(r => r.IsUsed).ThenBy(r => r.RouteDisplay).ToList();

        // 販売中の船 (RSI ストア)。保有列は名寄せ後の現在保有型で照合
        _storeRows = _storeSkus.Select(k => new StoreRow
        {
            Category = k.Category,
            Name = k.Name,
            NativePriceDisplay = k.NativePriceDisplay,
            IsWarbond = k.IsWarbond,
            StockLevel = k.StockLevel,
            OwnedDisplay = heldNames.Contains(_hangarService.NormalizeShipName(k.Name)) ? "保有" : "",
        }).ToList();

        RefreshUpgradeShipGrid();   // CCU 行 (_upgradeCcus の状態) も保有船グリッドに差し込まれる
        RefreshStoreGrid();
        RefreshMyShipRows();   // 所持船の保有数・由来・Upgradable 列も同期状態に合わせる

        // melt シミュレーションパネルのチェックを _soldPledgeIds に合わせる (MeltPledgeRow_PropertyChanged は一致するので再入しない)。
        // 適用中 (どの船行かの AppliedCcuIds に含まれる) の CCU pledge はチェック不可 (Undo 後に売却できる)
        var appliedPledgeIds = new HashSet<string>(shipRows.SelectMany(s => s.AppliedCcuIds), StringComparer.OrdinalIgnoreCase);
        foreach (var m in _meltPledges)
        {
            m.Selected = !string.IsNullOrEmpty(m.PledgeId) && _soldPledgeIds.Contains(m.PledgeId);
            m.CanSelect = m.Meltable && !(!string.IsNullOrEmpty(m.PledgeId) && appliedPledgeIds.Contains(m.PledgeId));
        }

        var appliedTotal = _allCcus.Where(c => used.Contains(c.PledgeId)).Sum(c => c.PriceCents);
        var usableTotal = _upgradeCcus.Where(r => r.StateDisplay == "使用可")
            .Join(_allCcus, r => r.PledgeId, c => c.PledgeId, (r, c) => c.PriceCents).Sum();
        var unusableTotal = _upgradeCcus.Where(r => r.IsUnusable)
            .Join(_allCcus, r => r.PledgeId, c => c.PledgeId, (r, c) => c.PriceCents).Sum();

        var totals = _hangarService.GetTotals();
        var storeCredit = totals.StoreCreditCents.HasValue ? $"${totals.StoreCreditCents.Value / 100.0:N2}" : "未取得";
        var buyback = totals.BuybackTokens.HasValue ? totals.BuybackTokens.Value.ToString() : "未取得";

        // クレジット管理 (R-01: 売却 Credit = Σ 売却 pledge 額 + Σ その pledge に適用中の CCU 額。R-09: meltable=false は除外)
        long sellPledgeCredit = 0;
        var soldPledgeCount = 0;
        var losesLti = false;
        foreach (var id in _soldPledgeIds)
        {
            if (!_pledgeById.TryGetValue(id, out var p) || !p.Meltable) continue;
            soldPledgeCount++;
            sellPledgeCredit += p.ValueCents;
            if (p.Insurance == HangarInsurance.Lti) losesLti = true;
        }
        var sellCcuCredit = shipRows.Where(s => s.IsSold)
            .SelectMany(s => s.AppliedCcuIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Sum(id => ccuById.TryGetValue(id, out var c) ? c.PriceCents : 0);
        var sellCredit = sellPledgeCredit + sellCcuCredit;
        var lostShips = shipRows.Count(s => s.IsSold && s.Meltable);   // Credit と同様、melt 不可の pledge は数えない
        var totalCredit = totals.StoreCreditCents.HasValue
            ? $"${(totals.StoreCreditCents.Value + sellCredit) / 100.0:N2}"
            : $"${sellCredit / 100.0:N2} (Store Credit 未取得)";
        txtSellLtiWarning.Visibility = losesLti ? Visibility.Visible : Visibility.Collapsed;

        txtUpgradeStatus.Text = $"保有船 {shipRows.Count} 機 / 権利 {_allCcus.Count} 本";
        ApplyUnresolvedWarning();
        txtUpgradeSummary.Text =
            $"適用中 ${appliedTotal / 100.0:N2}  |  未使用 ${usableTotal / 100.0:N2}  |  使用不可 ${unusableTotal / 100.0:N2}" +
            $"  |  pledge {totals.PledgeCount} / 総 melt 額 ${totals.TotalMeltValueCents / 100.0:N2} / melt 可能額 ${totals.MeltableValueCents / 100.0:N2}" +
            "   ※ 権利の行をダブルクリックすると標準CCU額を入力でき、割引率が出ます" +
            $"\n現在の Store Credit {storeCredit}  |  売却で得られる Credit ${sellCredit / 100.0:N2} (pledge {soldPledgeCount} 件)" +
            $"  |  合計 {totalCredit}  |  失う機体 {lostShips} 機  |  LTI 喪失{(losesLti ? "あり" : "なし")}" +
            $"  |  Buy Back 残トークン {buyback}";
    }

    private void UpgradeApply_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not UpgradeShipRow row) return;
        if (row.IsCcu)
        {
            // CCU 行の Upgrade: その CCU を親の船行に適用する (ダイアログ無し)
            if (!row.UpgradeEnabled || row.CcuParent == null) return;
            ApplyUpgradeToRow(row.CcuParent, row.CcuPledgeId);
            return;
        }
        ApplyUpgradeToRow(row);
    }

    // 指定した権利 1 本を保有船 1 行に適用する (CCU 行の Upgrade ボタン用。ダイアログ無し)。適用したら true
    private bool ApplyUpgradeToRow(UpgradeShipRow row, string ccuId)
    {
        if (row.IsGroupHeader || row.IsCcu || row.IsSold) return false;   // ヘッダ行・CCU 行・売却 (melt 予定) 行には適用しない
        if (string.IsNullOrEmpty(ccuId)) return false;

        var used = new HashSet<string>(_upgradeShips.SelectMany(s => s.AppliedCcuIds), StringComparer.OrdinalIgnoreCase);
        if (used.Contains(ccuId)) return false;
        var ccu = _allCcus.FirstOrDefault(c => c.PledgeId.Equals(ccuId, StringComparison.OrdinalIgnoreCase));
        if (ccu == null) return false;
        if (_soldPledgeIds.Contains(ccu.PledgeId)) return false;   // 売却予定 (melt) の権利は適用しない

        var current = _hangarService.NormalizeShipName(row.CurrentName);
        if (!_hangarService.NormalizeShipName(ccu.FromShip).Equals(current, StringComparison.OrdinalIgnoreCase)) return false;

        row.AppliedCcuIds.Add(ccu.PledgeId);
        RecomputeUpgradeSim();
        return true;
    }

    // 保有船 1 行に使用可能な権利を 1 本適用する (候補が複数なら選択ダイアログ)。適用したら true
    private bool ApplyUpgradeToRow(UpgradeShipRow row)
    {
        if (row.IsGroupHeader || row.IsCcu || row.IsSold) return false;   // ヘッダ行・CCU 行・売却 (melt 予定) 行には適用しない
        if (!row.CanUpgrade) return false;

        var used = new HashSet<string>(_upgradeShips.SelectMany(s => s.AppliedCcuIds), StringComparer.OrdinalIgnoreCase);
        var current = _hangarService.NormalizeShipName(row.CurrentName);
        // 売却予定 (Sell / melt パネル) の CCU は候補から除く (RecomputeUpgradeSim の CanUpgrade / usable 判定と同じ条件)
        var candidates = _allCcus.Where(c => !used.Contains(c.PledgeId)
            && !(!string.IsNullOrEmpty(c.PledgeId) && _soldPledgeIds.Contains(c.PledgeId))
            && _hangarService.NormalizeShipName(c.FromShip).Equals(current, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0) return false;

        HangarCcu? chosen;
        if (candidates.Count == 1)
        {
            chosen = candidates[0];
        }
        else
        {
            chosen = PickCcu(candidates);
            if (chosen == null) return false;
        }

        row.AppliedCcuIds.Add(chosen.PledgeId);
        RecomputeUpgradeSim();
        return true;
    }

    // 「購入可」列: ストア (Standalone Ships / Upgrades) にこの船より税抜価格が高い船があれば件数を出す
    private string StoreUpgradeDisplayFor(string shipName)
    {
        var t = _hangarService.StoreUpgradeTargets(shipName);
        if (t == null || t.Value.Count == 0) return "";
        return t.Value.HasWarbond ? $"Warbond あり ({t.Value.Count})" : $"購入可 ({t.Value.Count})";
    }

    // 最後に適用した1本だけを外す (連鎖の途中まで戻れる)。
    // 使用中の CCU 行の Undo は、その CCU が親の船行の末尾 (最後に適用した 1 本) のときだけ外す
    private void UpgradeUndo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not UpgradeShipRow row) return;
        if (row.IsCcu)
        {
            if (!row.UndoEnabled || row.CcuParent == null) return;
            var ids = row.CcuParent.AppliedCcuIds;
            if (ids.Count == 0 || !ids[ids.Count - 1].Equals(row.CcuPledgeId, StringComparison.OrdinalIgnoreCase)) return;
            ids.RemoveAt(ids.Count - 1);
            RecomputeUpgradeSim();
            return;
        }
        if (!row.HasApplied) return;
        row.AppliedCcuIds.RemoveAt(row.AppliedCcuIds.Count - 1);
        RecomputeUpgradeSim();
    }

    // 候補が複数あるときに選ばせる簡易ダイアログ
    private HangarCcu? PickCcu(List<HangarCcu> candidates)
    {
        var win = new Window
        {
            Title = "適用する権利を選択",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
        };
        var panel = new StackPanel { Margin = new Thickness(12) };
        HangarCcu? result = null;
        foreach (var c in candidates)
        {
            var b = new Button
            {
                Content = $"{c.RouteDisplay}   {c.PriceDisplay}{(c.Warbond ? "  (Warbond)" : "")}",
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(10, 6, 10, 6),
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            var captured = c;
            b.Click += (_, _) => { result = captured; win.DialogResult = true; };
            panel.Children.Add(b);
        }
        win.Content = panel;
        win.ShowDialog();
        return result;
    }

    // 保有船グリッドの CCU (アップグレード) 行をダブルクリックしたら標準CCU額を入力させ、割引率を出せるようにする (船行では何もしない)
    private void UpgradeCcu_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // 行内のボタン (Upgrade / Undo / Sell / 装備) 上のダブルクリック (連打) では開かない: 祖先に Button があれば何もしない
        for (var d = e.OriginalSource as DependencyObject; d != null;
             d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                 ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                 : LogicalTreeHelper.GetParent(d))
        {
            if (d is Button) return;
        }
        if (dgUpgradeShips.SelectedItem is not UpgradeShipRow row || !row.IsCcu) return;
        var ccu = _allCcus.FirstOrDefault(c => c.PledgeId.Equals(row.CcuPledgeId, StringComparison.OrdinalIgnoreCase));
        if (ccu == null) return;

        var dlg = new InputDialog($"{ccu.RouteDisplay} の標準CCU額 (USD) を入力してください。\n0 を入力すると割引率を消します。")
        {
            Owner = this,
        };
        if (dlg.ShowDialog() != true) return;

        var text = dlg.ResponseText.Replace("$", "").Replace(",", "").Trim();
        long cents = 0;
        if (!string.IsNullOrEmpty(text))
        {
            if (!decimal.TryParse(text, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var usd))
            {
                MessageBox.Show("金額として解釈できませんでした。", "入力エラー");
                return;
            }
            cents = (long)Math.Round(usd * 100m, MidpointRounding.AwayFromZero);
        }

        try
        {
            _hangarService.SetCacheDir(WorkDir);
            _hangarService.SetCcuStdPrice(ccu.PledgeId, cents);
            ccu.StdPriceCents = cents;
            RecomputeUpgradeSim();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存に失敗しました: {ex.Message}", "エラー");
        }
    }

    // 保有船グリッド「マーク」列の ComboBox。ItemsSource 再代入時にも発火するので、値が同じなら何もしない
    private void UpgradeMark_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.DataContext is not UpgradeShipRow row) return;
        if (row.IsGroupHeader) return;   // ヘッダ行は船ではないのでマークを保存しない
        if (row.IsNonPledge) return;     // 所持船由来の行は pledge ではないため保存キーが無い
        var selected = combo.SelectedItem as string ?? "";
        if (selected == row.MarkDisplay) return;

        var mark = selected switch
        {
            "要る" => HangarShipMark.Keep,
            "要らない" => HangarShipMark.Drop,
            "保留" => HangarShipMark.Hold,
            _ => HangarShipMark.None,
        };

        try
        {
            _hangarService.SetCacheDir(WorkDir);
            _hangarService.SetMark(row.PledgeId, row.Name, mark);
            row.Mark = mark;
            RecomputeUpgradeSim();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"マークの保存に失敗しました: {ex.Message}", "エラー");
        }
    }

    // CCU プランナー: 保有船 + 権利から適用/melt/使用不可の提案を作る。
    // 手動シミュレーション状態を反映する: 保有船は _upgradeShips の子行/単独行 (売却済みは除外) を CurrentName (適用後の船名) で、
    // CCU は未使用 (どの行の AppliedCcuIds にも無い) ものだけ渡す
    private void UpgradePlan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _hangarService.SetCacheDir(WorkDir);
            var applied = new HashSet<string>(_upgradeShips.SelectMany(s => s.AppliedCcuIds), StringComparer.OrdinalIgnoreCase);
            var ships = _upgradeShips
                .Where(r => !r.IsGroupHeader && !r.IsSold && !r.IsNonPledge)   // Hangar 未一致の所持船は CCU の対象外
                .Select(r =>
                {
                    _pledgeById.TryGetValue(r.PledgeId, out var p);
                    return new HangarShipInstance
                    {
                        PledgeId = r.PledgeId,
                        PledgeName = r.PledgeName,
                        Name = r.CurrentName,
                        Insurance = p?.Insurance ?? HangarInsurance.Unknown,
                        Matrix = _hangarService.ResolveShip(r.CurrentName),
                        Mark = r.Mark,
                        OriginDisplay = r.OriginDisplay,
                        SameTypeCount = r.SameTypeCount,
                        PledgeShipNames = r.PledgeShipNames,
                    };
                })
                .ToList();
            var ccus = _allCcus.Where(c => !applied.Contains(c.PledgeId)).ToList();
            var plan = _hangarService.BuildPlan(ships, ccus);
            var ccuById = _allCcus.Where(c => !string.IsNullOrEmpty(c.PledgeId))
                .GroupBy(c => c.PledgeId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var rows = new List<PlanRow>();
            foreach (var a in plan.Applies)
            {
                rows.Add(new PlanRow
                {
                    Kind = "適用",
                    Detail = $"{a.FromShip} → {a.ToShip}（{a.CcuIds.Count}本、保険 {InsuranceLabel(a.ResultInsurance)}）",
                    AmountDisplay = $"${a.TotalCostCents / 100.0:N2}",
                    Reason = a.Rationale,
                });
            }
            foreach (var m in plan.Melts)
            {
                rows.Add(new PlanRow
                {
                    Kind = "melt",
                    Detail = ccuById.TryGetValue(m.CcuId, out var c) ? c.RouteDisplay : m.CcuId,
                    AmountDisplay = $"${m.ValueCents / 100.0:N2}",
                    Reason = m.Reason,
                });
            }
            foreach (var u in plan.Unusable)
            {
                rows.Add(new PlanRow
                {
                    Kind = "使用不可",
                    Detail = ccuById.TryGetValue(u.CcuId, out var c) ? c.RouteDisplay : u.CcuId,
                    AmountDisplay = "",
                    Reason = u.Reason,
                });
            }

            dgPlan.ItemsSource = null;
            dgPlan.ItemsSource = rows;
            txtPlanSummary.Text = $"適用 {plan.Applies.Count} 件 / melt {plan.Melts.Count} 件 / 使用不可 {plan.Unusable.Count} 件"
                + (plan.TimedOut ? "  ※ 探索がタイムアウトしました（部分結果）" : "");
        }
        catch (Exception ex)
        {
            txtPlanSummary.Text = $"計算エラー: {ex.Message}";
        }
    }

    private static string InsuranceLabel(HangarInsurance ins) => ins switch
    {
        HangarInsurance.Lti => "LTI",
        HangarInsurance.Months120 => "120ヶ月",
        HangarInsurance.Months6 => "6ヶ月",
        _ => "不明",
    };

    // melt シミュレーション: チェックした pledge を melt した場合の Credit / 現金 / 失うものを出す
    private void MeltSim_Click(object sender, RoutedEventArgs e)
    {
        var targetText = (txtMeltTarget.Text ?? "").Replace("$", "").Replace(",", "").Trim();
        if (!decimal.TryParse(targetText, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var targetUsd))
        {
            MessageBox.Show("目標価格を USD の数値で入力してください。", "入力エラー");
            return;
        }

        var selectedIds = _soldPledgeIds.ToList();   // Sell ボタン／パネルのチェックと双方向に同期している

        try
        {
            _hangarService.SetCacheDir(WorkDir);
            var result = _hangarService.SimulateMelt(new MeltSimInput
            {
                MeltPledgeIds = selectedIds,
                MeltCcuIds = new List<string>(),
                TargetPriceCents = (long)Math.Round(targetUsd * 100m, MidpointRounding.AwayFromZero),
            });

            var pledgeName = _meltPledges.ToDictionary(p => p.PledgeId, p => p.Name, StringComparer.Ordinal);
            var ccuRoute = _allCcus.Where(c => !string.IsNullOrEmpty(c.PledgeId))
                .GroupBy(c => c.PledgeId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().RouteDisplay, StringComparer.Ordinal);
            string PName(string id) => pledgeName.TryGetValue(id, out var n) && !string.IsNullOrEmpty(n) ? n : id;
            string CRoute(string id) => ccuRoute.TryGetValue(id, out var r) ? r : id;

            var sb = new StringBuilder();
            sb.AppendLine($"Credit: ${result.CreditCents / 100.0:N2}");
            sb.AppendLine($"現金 (税10%込): ${result.CashCents / 100.0:N2}");
            sb.AppendLine($"失う機体: {(result.LostShips.Count > 0 ? string.Join(", ", result.LostShips) : "なし")}");
            sb.AppendLine($"要チケット pledge: {(result.NeedsTicketPledges.Count > 0 ? string.Join(", ", result.NeedsTicketPledges.Select(PName)) : "なし")}");
            sb.AppendLine($"使用不能になる CCU: {(result.BlockedCcuIds.Count > 0 ? string.Join(", ", result.BlockedCcuIds.Select(CRoute)) : "なし")}");
            if (result.NotMeltablePledges.Count > 0)
                sb.AppendLine($"melt 不可: {string.Join(", ", result.NotMeltablePledges.Select(PName))}");

            txtMeltResult.Text = sb.ToString().TrimEnd();
            txtMeltLtiWarning.Visibility = result.LosesLti ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            txtMeltResult.Text = $"計算エラー: {ex.Message}";
            txtMeltLtiWarning.Visibility = Visibility.Collapsed;
        }
    }

    // reloadUpgrade=false のときは LoadUpgradeData を呼ばない (HangarSync_Click のように直前に LoadUpgradeData 済みの場合の二重読込回避)。
    // reloadUpgrade=true のときは所持船グリッドの ItemsSource をここでは設定せず、LoadUpgradeData → RecomputeUpgradeSim → RefreshMyShipRows の
    // 再設定に任せる (二重設定の回避)。ただし LoadUpgradeData が途中で例外に入って false を返した場合は
    // RefreshMyShipRows に達していない可能性があるので、従来どおりここで代入する (取りこぼし防止)
    private void RefreshMyShips(bool reloadUpgrade = true)
    {
        _tradeService.SetCacheDir(WorkDir);
        _tradeService.LoadMyShips();
        FillMissingMyShipManufacturers();   // メーカーが空の行を Ship Matrix から埋める (まとめて 1 回で書き、内部で読み直す)
        PopulateShipMfrCombo();             // 「船を追加」のメーカー候補
        PopulateShipNameCombo();            // 「船を追加」の船名候補 (3 文字以上で絞り込み)
        if (!reloadUpgrade)
        {
            dgMyShips.ItemsSource = null;
            dgMyShips.ItemsSource = _tradeService.MyShips.Select(ToMyShipRow).ToList();
        }
        txtMyShipStatus.Text = $"所持船: {_tradeService.MyShips.Count} 隻";
        RefreshCommodityShipCombo();
        if (reloadUpgrade && !LoadUpgradeData())   // 所持船由来の行 (IsNonPledge) をアップグレード管理へ即時反映 (削除済みの行を残さない)。正常終了時は所持船グリッドも内部で再設定される
        {
            dgMyShips.ItemsSource = null;
            dgMyShips.ItemsSource = _tradeService.MyShips.Select(ToMyShipRow).ToList();
        }
    }

    // 「船を追加」のメーカー候補を Ship Matrix のメーカー名 (重複なし) で埋める。
    // 既に埋まっているときは何もしない (ItemsSource を差し替えると入力中の文字が消えるため)
    private void PopulateShipMfrCombo()
    {
        if (cmbAddShipMfr == null) return;   // XAML 読込中
        if (cmbAddShipMfr.ItemsSource is IEnumerable<string> cur && cur.Any()) return;
        try
        {
            _hangarService.SetCacheDir(WorkDir);
            var names = _hangarService.LoadShipMatrix()
                .Select(s => s.ManufacturerName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (names.Count > 0) cmbAddShipMfr.ItemsSource = names;
        }
        catch (Exception ex) { Log($"[Ship] メーカー一覧の読み込みに失敗: {ex.Message}"); }
    }

    // 「船を追加」の船名候補を Ship Matrix の船名で埋める。ItemsSource は一度だけ設定し、
    // 絞り込みは Items.Filter で行う (編集可能 ComboBox は ItemsSource を差し替えると入力中の文字が消えるため)
    private void PopulateShipNameCombo()
    {
        if (cmbAddShipName == null) return;   // XAML 読込中
        if (cmbAddShipName.ItemsSource is IEnumerable<string> cur && cur.Any()) return;
        try
        {
            _hangarService.SetCacheDir(WorkDir);
            var names = _hangarService.LoadShipMatrix()
                .Select(s => s.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (names.Count == 0) return;

            _suppressAddShipNameEvents = true;
            try
            {
                var keep = cmbAddShipName.Text;
                cmbAddShipName.ItemsSource = names;
                cmbAddShipName.Text = keep;
            }
            finally { _suppressAddShipNameEvents = false; }
        }
        catch (Exception ex) { Log($"[Ship] 船名候補の読み込みに失敗: {ex.Message}"); }
    }

    // 船名欄の入力。3 文字以上で候補を絞って開き、船名が Ship Matrix で解決できればメーカーを自動で入れる
    private void AddShipName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressAddShipNameEvents) return;
        FilterAddShipNameCandidates();
        AutoFillShipManufacturer();
    }

    private void AddShipName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressAddShipNameEvents) return;
        AutoFillShipManufacturer();
    }

    // 3 文字以上のときだけ候補を絞ってドロップダウンを開く。
    // ItemsSource は差し替えず CollectionView の Filter で絞る (入力中の文字を消さないため)。
    // Filter で SelectedItem が外れると編集可能 ComboBox は Text を巻き戻すことがあるので、Text は退避して戻す
    private void FilterAddShipNameCandidates()
    {
        if (cmbAddShipName?.ItemsSource == null) return;
        var text = (cmbAddShipName.Text ?? "").Trim();
        var keep = cmbAddShipName.Text;

        if (text.Length < 3)
        {
            cmbAddShipName.Items.Filter = null;
            cmbAddShipName.IsDropDownOpen = false;
        }
        else
        {
            cmbAddShipName.Items.Filter = o => o is string s && s.Contains(text, StringComparison.OrdinalIgnoreCase);
            cmbAddShipName.IsDropDownOpen = cmbAddShipName.Items.Count > 0;
        }

        if (!string.Equals(cmbAddShipName.Text, keep, StringComparison.Ordinal))
        {
            _suppressAddShipNameEvents = true;
            try
            {
                cmbAddShipName.Text = keep;
                if (cmbAddShipName.Template?.FindName("PART_EditableTextBox", cmbAddShipName) is TextBox tb)
                    tb.CaretIndex = keep?.Length ?? 0;
            }
            finally { _suppressAddShipNameEvents = false; }
        }
    }

    // 船名から Ship Matrix でメーカーを解決してメーカー欄に入れる。
    // 解決できないときは今入っている値を消さずそのままにする (手入力を壊さない)
    private void AutoFillShipManufacturer()
    {
        if (cmbAddShipName == null || cmbAddShipMfr == null) return;
        var name = (cmbAddShipName.Text ?? "").Trim();
        if (name.Length == 0) return;
        try
        {
            _hangarService.SetCacheDir(WorkDir);
            var mfr = _hangarService.ResolveShip(name)?.ManufacturerName;
            if (!string.IsNullOrWhiteSpace(mfr)) cmbAddShipMfr.Text = mfr;
        }
        catch { }
    }

    // 船名欄をプログラムから設定する。候補ドロップダウンを開かず、メーカーの自動入力も走らせない
    // (呼び出し側がメーカーを別途設定するため)
    private void SetAddShipName(string name)
    {
        if (cmbAddShipName == null) return;
        _suppressAddShipNameEvents = true;
        try
        {
            if (cmbAddShipName.ItemsSource != null) cmbAddShipName.Items.Filter = null;
            cmbAddShipName.Text = name;
            cmbAddShipName.IsDropDownOpen = false;
        }
        finally { _suppressAddShipNameEvents = false; }
    }

    // メーカーが空の所持船を Ship Matrix (船名から自動判別) で埋めて my_ships に保存する。
    // 既に入っているメーカーは上書きしない。まとめて 1 回で書くので、対象が多くても DB の読み直しは 1 回で済む
    private void FillMissingMyShipManufacturers()
    {
        try
        {
            _hangarService.SetCacheDir(WorkDir);
            var updates = new List<(int Id, string Manufacturer)>();
            foreach (var s in _tradeService.MyShips)
            {
                if (!string.IsNullOrWhiteSpace(s.Manufacturer)) continue;
                var mfr = _hangarService.ResolveShip(s.Name)?.ManufacturerName;
                if (string.IsNullOrWhiteSpace(mfr)) continue;
                updates.Add((s.Id, mfr));
            }
            if (updates.Count == 0) return;

            var filled = _tradeService.UpdateMyShipManufacturers(updates);
            if (filled > 0) Log($"[Ship] メーカーを自動補完: {filled} 件");
        }
        catch (Exception ex) { Log($"[Ship] メーカーの自動補完に失敗: {ex.Message}"); }
    }

    // DB を読み直さず、現在の _tradeService.MyShips を表示行に写し直す (同期状態・シミュレーション状態の反映用)
    private void RefreshMyShipRows()
    {
        if (dgMyShips == null) return;   // XAML 読込中
        var selectedId = (dgMyShips.SelectedItem as MyShipRow)?.Id;
        dgMyShips.ItemsSource = null;
        var rows = _tradeService.MyShips.Select(ToMyShipRow).ToList();
        dgMyShips.ItemsSource = rows;
        if (selectedId != null)
            dgMyShips.SelectedItem = rows.FirstOrDefault(r => r.Id == selectedId.Value);
    }

    // 所持船 (手入力) 1 件を表示行にする。同期済み保有船と正規化名で照合し、保有数・由来・Upgradable・販売・購入可を埋める
    private MyShipRow ToMyShipRow(MyShipEntry e)
    {
        var row = new MyShipRow
        {
            Id = e.Id,
            Name = e.Name,
            Manufacturer = e.Manufacturer,
            Scu = e.Scu,
            Notes = e.Notes,
            AddedAt = e.AddedAt,
        };

        var matched = MatchHangarRowsForMyShip(e.Name);

        row.HangarCount = matched.Count;
        row.HangarOriginDisplay = string.Join(" / ", matched.Select(s => s.OriginDisplay).Where(o => !string.IsNullOrEmpty(o)));
        // Hangar に一致しない船で、メモが "[ゲーム内購入]" で始まるものは aUEC 購入 (未同期ではない)
        if (matched.Count == 0 && IsInGameNotes(e.Notes))
        {
            row.IsInGame = true;
            row.HangarOriginDisplay = "ゲーム内購入";
        }
        row.HangarCanUpgrade = matched.Any(s => s.CanUpgrade);
        row.HangarUpgradableDisplay = row.HangarCanUpgrade ? "○" : "";
        row.HangarSaleDisplay = _hangarService.FindStoreShipWarbond(e.Name) != null ? "Warbond 販売中"
            : _hangarService.FindStoreShip(e.Name) != null ? "販売中" : "";
        row.HangarStoreUpgradeDisplay = StoreUpgradeDisplayFor(e.Name);

        // ローナー機 (LoadLoaners は HangarService 側でキャッシュされるので行ごとに呼んでも DB は読み直さない)
        if (_hangarService.LoadLoaners().TryGetValue(_hangarService.NormalizeShipName(e.Name), out var loaners))
            row.LoanerDisplay = string.Join(" / ", loaners);

        // ツールチップは初回表示時に生成 (所持船→保有船インスタンスの選び方は OpenLoadout_Click と同じ FindHangarRowForMyShip)
        var hangarRow = FindHangarRowForMyShip(row);
        if (hangarRow != null)
        {
            row.TooltipFactory = () => hangarRow.Tooltip;
        }
        else
        {
            // 未同期: Ship Matrix の情報のみ
            var myId = e.Id;
            var myName = e.Name;
            row.TooltipFactory = () =>
            {
                var tmp = new HangarShipInstance { Name = myName, Matrix = _hangarService.ResolveShip(myName) };
                return _hangarService.BuildShipTooltip(tmp, _hangarEquip, $"my|{myId}");
            };
        }
        return row;
    }

    // 所持船名 (正規化名) に一致する保有船行を _upgradeShips の表示順で返す (ヘッダ行・CCU 行・所持船由来の行 (IsNonPledge) は除く)
    private List<UpgradeShipRow> MatchHangarRowsForMyShip(string shipName)
    {
        var norm = _hangarService.NormalizeShipName(shipName);
        return _upgradeShips
            .Where(s => !s.IsGroupHeader && !s.IsCcu && !s.IsNonPledge
                && _hangarService.NormalizeShipName(s.Name).Equals(norm, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    // 所持船 1 件に対応する保有船インスタンス = _upgradeShips の表示順で最初に正規化名が一致する船行 (ヘッダ行・CCU 行は除く)。
    // ツールチップ (ToMyShipRow) と「装備」ボタン (OpenLoadout_Click) で同じ船を指すよう、両方からこれを使う。無ければ null
    private UpgradeShipRow? FindHangarRowForMyShip(MyShipRow my) => MatchHangarRowsForMyShip(my.Name).FirstOrDefault();

    // 所持船の「アップグレード」ボタン: アップグレード管理タブへ切り替え、一致する保有船に権利を適用する
    private void MyShipUpgrade_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not MyShipRow my) return;

        var norm = _hangarService.NormalizeShipName(my.Name);
        var target = _upgradeShips.FirstOrDefault(s => !s.IsGroupHeader && !s.IsSold && s.CanUpgrade
            && _hangarService.NormalizeShipName(s.Name).Equals(norm, StringComparison.OrdinalIgnoreCase));

        tabShipManagement.SelectedItem = tabUpgradeManage;
        if (target == null)
        {
            txtUpgradeStatus.Text = $"「{my.Name}」に使える権利はありません";
            return;
        }

        // 折りたたまれたグループの子行なら展開してから選択できるようにする
        if (target.IsChild && !string.IsNullOrEmpty(target.GroupPledgeId)) _expandedPledgeIds.Add(target.GroupPledgeId);

        ApplyUpgradeToRow(target);   // 内部で RecomputeUpgradeSim → RefreshMyShipRows まで行う

        dgUpgradeShips.SelectedItem = target;
        dgUpgradeShips.ScrollIntoView(target);
    }

    // 同期済み保有船のうち、正規化名が所持船に無いものを所持船に追加する (同型は 1 件)
    private void ImportHangarShips_Click(object sender, RoutedEventArgs e) => ImportHangarShipsIntoMyShips(confirm: true);

    // 同期済み保有船を所持船へ追加する。confirm=true のときだけ確認ダイアログを出す。追加件数を返す。
    // 既存の所持船行は変更・削除しない (手入力の保護)。
    // reloadUpgrade は最後の RefreshMyShips に渡す (HangarSync_Click は直前に LoadUpgradeData 済みなので false)
    private int ImportHangarShipsIntoMyShips(bool confirm, bool reloadUpgrade = true)
    {
        if (_hangarShips.Count == 0)
        {
            if (confirm)
                MessageBox.Show("同期済みの保有船がありません。先に [My Hangar と同期] を実行してください。", "所持船に反映");
            return 0;
        }

        _tradeService.SetCacheDir(WorkDir);
        _tradeService.LoadMyShips();
        var existing = new HashSet<string>(
            _tradeService.MyShips.Select(m => _hangarService.NormalizeShipName(m.Name)),
            StringComparer.OrdinalIgnoreCase);

        var toAdd = new List<HangarShipInstance>();
        foreach (var s in _hangarShips.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            var norm = _hangarService.NormalizeShipName(s.Name);
            if (existing.Contains(norm)) continue;
            existing.Add(norm);
            toAdd.Add(s);
        }

        if (toAdd.Count == 0)
        {
            if (confirm)
                MessageBox.Show("同期済み保有船はすべて所持船に登録されています。", "所持船に反映");
            return 0;
        }

        if (confirm)
        {
            var preview = string.Join("\n", toAdd.Take(20).Select(s => $"・{s.Name}"));
            if (toAdd.Count > 20) preview += $"\n… 他 {toAdd.Count - 20} 件";
            var answer = MessageBox.Show(
                $"所持船に無い保有船 {toAdd.Count} 件を追加しますか？\n\n{preview}",
                "所持船に反映", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return 0;
        }

        foreach (var s in toAdd)
        {
            var uex = _tradeService.FindUexShip(s.Name);
            var scu = uex?.Scu ?? 0;
            var mfr = s.Matrix?.ManufacturerName ?? "";
            if (string.IsNullOrEmpty(mfr)) mfr = uex?.Manufacturer ?? "";
            _tradeService.AddMyShip(s.Name, mfr, scu, s.OriginDisplay);
            Log($"[Ship] 追加 (Hangar 反映): {s.Name} ({scu} SCU) {s.OriginDisplay}");
        }

        RefreshMyShips(reloadUpgrade);
        txtMyShipStatus.Text = $"所持船: {_tradeService.MyShips.Count} 隻 | Hangar から {toAdd.Count} 件追加";
        return toAdd.Count;
    }

    // === 装備サブタブ: 所持コンポーネント (my_components) ===

    // my_components を読み直してグリッドに出す。種別コンボは初回のみ埋める
    private void RefreshMyComponents()
    {
        if (dgMyComponents == null) return;   // XAML 読込中
        try
        {
            _hangarService.SetCacheDir(WorkDir);
            _myComponents = _hangarService.LoadMyComponents();
            _myComponentRows = _myComponents
                .Select(c => new MyComponentRow(c) { OnUsableChanged = MyComponentUsable_Changed })
                .ToList();
            var selectedId = (dgMyComponents.SelectedItem as MyComponentRow)?.Id;
            dgMyComponents.ItemsSource = null;
            dgMyComponents.ItemsSource = _myComponentRows;
            if (selectedId != null)
                dgMyComponents.SelectedItem = _myComponentRows.FirstOrDefault(c => c.Id == selectedId.Value);
            txtCompStatus.Text = _hangarEquip == null
                ? "gamedata_cache.db がありません (コンポーネントの検索・追加はできません)"
                : $"所持コンポーネント: {_myComponents.Count} 件";
            PopulateCompTypeCombo();
        }
        catch (Exception ex)
        {
            txtCompStatus.Text = $"読み込みエラー: {ex.Message}";
        }
    }

    private void PopulateCompTypeCombo()
    {
        if (_compTypeComboPopulated || _hangarEquip == null) return;
        var categories = _hangarEquip.GetShipComponentCategories();
        cmbCompType.ItemsSource = categories;
        if (categories.Count > 0) cmbCompType.SelectedIndex = 0;   // SelectionChanged → RefreshCompCandidates
        _compTypeComboPopulated = true;
    }

    private void CompType_Changed(object sender, SelectionChangedEventArgs e) => RefreshCompCandidates();
    private void CompSearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshCompCandidates();

    // 種別 + 検索語で items を引き、候補コンボに出す。種別が選ばれた／検索文字が変わったときだけ、その種別に絞って組み立てる。
    // 表示名は GetShipComponents 側で item_index.name 優先に解決済み (行ごとの GetItemByRecord は行わない)
    private void RefreshCompCandidates()
    {
        if (cmbCompCandidates == null) return;
        if (_hangarEquip == null || cmbCompType.SelectedItem is not EquipmentCategory cat)
        {
            cmbCompCandidates.ItemsSource = null;
            return;
        }
        try
        {
            var search = txtCompSearch.Text.Trim();
            var items = _hangarEquip.GetShipComponents(cat.Key, search.Length > 0 ? search : null);
            var list = new List<CompCandidate>(items.Count);
            foreach (var i in items)
            {
                var name = i.Name;
                var type = new ShipPortInfo { ItemType = i.ItemType }.TypeDisplay;
                var parts = new List<string> { name };
                if (i.SizeDisplay.Length > 0) parts.Add(i.SizeDisplay);
                var cls = MyComponent.GradeToClass(i.Grade);
                if (cls.Length > 0) parts.Add(cls);
                if (type.Length > 0) parts.Add($"[{type}]");
                list.Add(new CompCandidate { Item = i, Name = name, Display = string.Join(" ", parts) });
            }
            cmbCompCandidates.ItemsSource = list;
            if (list.Count > 0) cmbCompCandidates.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            txtCompStatus.Text = $"候補の取得エラー: {ex.Message}";
        }
    }

    private void AddMyComponent_Click(object sender, RoutedEventArgs e)
    {
        if (_hangarEquip == null)
        {
            MessageBox.Show("gamedata_cache.db がありません。コンポーネントを追加できません。", "装備");
            return;
        }
        if (cmbCompCandidates.SelectedItem is not CompCandidate cand)
        {
            MessageBox.Show("候補からコンポーネントを選択してください。", "入力エラー");
            return;
        }
        if (!int.TryParse(txtCompQty.Text.Trim(), out var qty) || qty < 1)
        {
            MessageBox.Show("数量は 1 以上の整数で入力してください。", "入力エラー");
            return;
        }

        var c = new MyComponent
        {
            ItemRecord = cand.Item.RecordName,
            ItemName = cand.Name,
            ItemType = cand.Item.ItemType,
            Size = cand.Item.Size,
            Grade = cand.Item.Grade,
            Quantity = qty,
            Usable = chkCompUsable.IsChecked == true,
            Notes = txtCompNotes.Text.Trim(),
        };
        try
        {
            _hangarService.SetCacheDir(WorkDir);
            _hangarService.AddMyComponent(c);
            Log($"[Equip] 追加: {c.ItemName} ({c.ItemType} {c.SizeDisplay} {c.GradeDisplay}) ×{qty}{(c.Usable ? "" : " (使用不可)")}");
            txtCompNotes.Text = "";
            txtCompQty.Text = "1";
            RefreshMyComponents();
            RefreshLoadoutGrid();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存に失敗しました: {ex.Message}", "エラー");
        }
    }

    private void DeleteMyComponent_Click(object sender, RoutedEventArgs e)
    {
        if (dgMyComponents.SelectedItem is not MyComponentRow row) return;
        var c = row.Model;
        if (MessageBox.Show($"「{c.ItemName}」({c.SizeDisplay} {c.GradeDisplay} ×{c.Quantity}) を削除しますか？", "確認", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try
        {
            _hangarService.SetCacheDir(WorkDir);
            _hangarService.DeleteMyComponent(c.Id);
            Log($"[Equip] 削除: {c.ItemName}");
            RefreshMyComponents();
            RefreshLoadoutGrid();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"削除に失敗しました: {ex.Message}", "エラー");
        }
    }

    // 「使用可」チェックの変更 (MyComponentRow.Usable の setter から) → DB へ書き戻す
    private void MyComponentUsable_Changed(MyComponentRow row)
    {
        try
        {
            _hangarService.SetCacheDir(WorkDir);
            _hangarService.UpdateMyComponent(row.Model);
            RefreshLoadoutGrid();   // 使用可否は装備候補に影響する
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存に失敗しました: {ex.Message}", "エラー");
        }
    }

    // === 装備サブタブ: 現在の装備 (ship_loadouts) ===

    // 船セレクタを GetShipInstances の結果 (+ 所持船側から開いた "my|" キーの船) で埋め直す。選択中のキーは維持する
    private void RefreshLoadoutShipCombo()
    {
        if (cmbLoadoutShip == null) return;   // XAML 読込中
        var prevKey = (cmbLoadoutShip.SelectedItem as LoadoutShipChoice)?.ShipKey;
        var items = _hangarShips
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.PledgeName, StringComparer.OrdinalIgnoreCase)
            .Select(s => new LoadoutShipChoice
            {
                ShipKey = s.InstanceKey,
                Name = s.Name,
                Manufacturer = s.Matrix?.ManufacturerName,
                Display = string.IsNullOrEmpty(s.PledgeName) ? s.Name : $"{s.Name} — {s.PledgeName}",
            })
            .ToList();
        foreach (var extra in _extraLoadoutShips)
            if (items.All(i => !i.ShipKey.Equals(extra.ShipKey, StringComparison.Ordinal))) items.Add(extra);

        _suppressLoadoutEvents = true;
        try
        {
            cmbLoadoutShip.ItemsSource = items;
            if (prevKey != null)
                cmbLoadoutShip.SelectedItem = items.FirstOrDefault(i => i.ShipKey.Equals(prevKey, StringComparison.Ordinal));
        }
        finally { _suppressLoadoutEvents = false; }
        RefreshLoadoutGrid();
    }

    private void LoadoutShip_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLoadoutEvents) return;
        RefreshLoadoutGrid();
    }

    // 選択中の船の全ポートを「デフォルト装備 / 現在の装備 / 候補」で出す
    private void RefreshLoadoutGrid()
    {
        if (dgLoadout == null) return;   // XAML 読込中
        var ship = cmbLoadoutShip.SelectedItem as LoadoutShipChoice;
        dgLoadout.ItemsSource = null;
        if (_hangarEquip == null)
        {
            txtLoadoutStatus.Text = "gamedata_cache.db がありません";
            return;
        }
        if (ship == null)
        {
            txtLoadoutStatus.Text = "船を選択してください";
            return;
        }
        try
        {
            _hangarService.SetCacheDir(WorkDir);
            ship.Record ??= _hangarService.ResolveShipRecordName(ship.Name, ship.Manufacturer);
            if (ship.Record == null)
            {
                txtLoadoutStatus.Text = $"「{ship.Name}」の装備データ (ships.record_name) を解決できません";
                return;
            }
            var rows = _hangarService.GetShipLoadout(ship.ShipKey, ship.Record, _hangarEquip)
                .Select(MakeLoadoutRow)
                .ToList();
            dgLoadout.ItemsSource = rows;
            txtLoadoutStatus.Text = $"{ship.Name}: ポート {rows.Count} / 設定済 {rows.Count(r => r.IsCustom)}";
        }
        catch (Exception ex)
        {
            txtLoadoutStatus.Text = $"読み込みエラー: {ex.Message}";
        }
    }

    // 候補 = 所持コンポーネントのうち同じ ItemType かつ Size <= PortSize (ポート size が 0 = 未収録のときはサイズで絞らない) かつ使用可。
    // 先頭は「(デフォルトに戻す)」
    private LoadoutRow MakeLoadoutRow(ShipPortLoadout p)
    {
        var row = new LoadoutRow
        {
            PortName = p.PortName,
            PortKey = p.PortKey,
            ItemType = p.ItemType,
            TypeDisplay = p.TypeDisplay,
            PortSize = p.PortSize,
            DefaultItemRecord = p.DefaultItemRecord,
            DefaultItemName = p.DefaultItemName,
            CurrentItemRecord = p.CurrentItemRecord,
            CurrentItemName = p.CurrentItemName,
            ItemSize = p.ItemSize,
            ItemGrade = p.ItemGrade,
        };

        var defaultChoice = new LoadoutChoice { IsDefault = true, ItemRecord = "", Display = "(デフォルトに戻す)" };
        row.Candidates.Add(defaultChoice);
        foreach (var c in _myComponents)
        {
            if (!c.Usable) continue;
            if (!c.ItemType.Equals(p.ItemType, StringComparison.OrdinalIgnoreCase)) continue;
            if (p.PortSize > 0 && c.Size > p.PortSize) continue;
            var parts = new List<string>();
            if (c.SizeDisplay.Length > 0) parts.Add(c.SizeDisplay);
            if (c.GradeDisplay.Length > 0) parts.Add(c.GradeDisplay);
            parts.Add(c.ItemName);
            parts.Add($"×{c.Quantity}");
            row.Candidates.Add(new LoadoutChoice { ItemRecord = c.ItemRecord, ItemName = c.ItemName, Display = string.Join(" ", parts) });
        }

        if (p.IsCustom)
        {
            var cur = row.Candidates.Skip(1).FirstOrDefault(c => c.ItemRecord.Equals(p.CurrentItemRecord, StringComparison.Ordinal));
            if (cur == null)
            {
                // 所持リストに無い (削除済み・使用不可にした・空スロット) 設定値はそのまま表示する
                var isEmptySlot = string.IsNullOrEmpty(p.CurrentItemRecord);
                var label = isEmptySlot ? "(空スロット)" : $"{p.CurrentItemName} (所持リストに無し)";
                cur = new LoadoutChoice { ItemRecord = p.CurrentItemRecord ?? "", ItemName = isEmptySlot ? "" : p.CurrentItemName, Display = label };
                row.Candidates.Insert(1, cur);
            }
            row.SelectedChoice = cur;
        }
        else
        {
            row.SelectedChoice = defaultChoice;
        }
        return row;
    }

    // 装備グリッドの ComboBox。ItemsSource 再代入時にも発火するので、値が同じなら何もしない
    private void LoadoutItem_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLoadoutEvents) return;
        if (sender is not ComboBox combo || combo.DataContext is not LoadoutRow row) return;
        if (combo.SelectedItem is not LoadoutChoice choice) return;
        if (ReferenceEquals(choice, row.SelectedChoice)) return;
        if (cmbLoadoutShip.SelectedItem is not LoadoutShipChoice ship) return;

        try
        {
            _hangarService.SetCacheDir(WorkDir);
            _hangarService.SetLoadoutItem(ship.ShipKey, row.PortKey, choice.IsDefault ? null : choice.ItemRecord);
            row.SelectedChoice = choice;
            Log($"[Equip] {ship.Name} {row.PortKey}: {(choice.IsDefault ? "デフォルトに戻す" : choice.Display)}");
            LoadUpgradeData();   // ツールチップを更新 (内部で RefreshLoadoutShipCombo → RefreshLoadoutGrid)
        }
        catch (Exception ex)
        {
            MessageBox.Show($"装備の保存に失敗しました: {ex.Message}", "エラー");
        }
    }

    private void LoadoutReset_Click(object sender, RoutedEventArgs e)
    {
        if (cmbLoadoutShip.SelectedItem is not LoadoutShipChoice ship) return;
        if (MessageBox.Show($"「{ship.Name}」の装備設定を全て削除し、デフォルト装備に戻しますか？", "確認", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try
        {
            _hangarService.SetCacheDir(WorkDir);
            _hangarService.ClearLoadout(ship.ShipKey);
            Log($"[Equip] {ship.Name}: 全てデフォルトに戻す");
            LoadUpgradeData();   // ツールチップを更新 (内部で RefreshLoadoutShipCombo → RefreshLoadoutGrid)
        }
        catch (Exception ex)
        {
            MessageBox.Show($"装備の保存に失敗しました: {ex.Message}", "エラー");
        }
    }

    // 保有船行 (アップグレード管理) / 所持船行の「装備」ボタン: 装備サブタブへ切り替え、船セレクタをその船に合わせる。
    // 所持船側は FindHangarRowForMyShip (ツールチップと同じ選び方) の保有船インスタンス、無ければ "my|{id}" キー (record は Matrix 名から解決)
    private void OpenLoadout_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        LoadoutShipChoice? target = null;

        if (btn.DataContext is UpgradeShipRow up)
        {
            if (up.IsGroupHeader) return;
            target = up.IsNonPledge
                ? FindOrAddMyLoadoutShipChoice(up.MyShipId, up.Name)   // 所持船由来の行 ("my|{id}")
                : FindLoadoutShipChoice(up.InstanceKey);
        }
        else if (btn.DataContext is MyShipRow my)
        {
            target = ResolveLoadoutChoiceForMyShip(my);
        }
        if (target == null) return;

        tabShipManagement.SelectedItem = tabLoadout;
        cmbLoadoutShip.SelectedItem = target;   // SelectionChanged → RefreshLoadoutGrid (同じ選択なら発火しないので明示的にも更新)
        RefreshLoadoutGrid();
    }

    // 所持船 1 件に対応する装備セレクタ項目。FindHangarRowForMyShip (ツールチップと同じ選び方) の保有船インスタンス、
    // 無ければ "my|{id}" キー。メーカー名は所持船の入力値ではなく Ship Matrix から自動解決する (所持船のメーカー欄は空のことが多い)
    private LoadoutShipChoice? ResolveLoadoutChoiceForMyShip(MyShipRow my)
    {
        var hangarRow = FindHangarRowForMyShip(my);
        LoadoutShipChoice? target = null;
        if (hangarRow != null) target = FindLoadoutShipChoice(hangarRow.InstanceKey);
        return target ?? FindOrAddMyLoadoutShipChoice(my.Id, my.Name);
    }

    private LoadoutShipChoice? FindLoadoutShipChoice(string shipKey)
        => (cmbLoadoutShip.ItemsSource as IEnumerable<LoadoutShipChoice>)
            ?.FirstOrDefault(i => i.ShipKey.Equals(shipKey, StringComparison.Ordinal));

    // 所持船のみの船 ("my|{my_ships.id}") の船セレクタ項目を返す。無ければ _extraLoadoutShips に加えてセレクタを埋め直してから返す
    private LoadoutShipChoice? FindOrAddMyLoadoutShipChoice(int myShipId, string shipName)
    {
        var key = $"my|{myShipId}";
        var target = FindLoadoutShipChoice(key);
        if (target != null) return target;

        var matrix = _hangarService.ResolveShip(shipName);
        _extraLoadoutShips.Add(new LoadoutShipChoice
        {
            ShipKey = key,
            Name = shipName,
            Manufacturer = matrix?.ManufacturerName,
            Display = $"{shipName} — 所持船",
        });
        RefreshLoadoutShipCombo();
        return FindLoadoutShipChoice(key);
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
            SetAddShipName(ship.Name);
            // UEX 側にメーカーが入っていないことがあるので、空なら船名から Ship Matrix で補う
            if (!string.IsNullOrWhiteSpace(ship.Manufacturer)) cmbAddShipMfr.Text = ship.Manufacturer;
            else AutoFillShipManufacturer();
            txtAddShipScu.Text = ship.Scu.ToString();
        }
    }

    private void AddMyShip_Click(object sender, RoutedEventArgs e)
    {
        var name = cmbAddShipName.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show("船名を入力してください。検索で候補を選択するか、直接入力してください。", "入力エラー");
            return;
        }

        // @vehicle_Name 解決
        name = _tradeService.ResolveVehicleName(name);

        var mfr = cmbAddShipMfr.Text.Trim();
        int.TryParse(txtAddShipScu.Text.Trim(), out var scu);
        var notes = ApplyInGameMarker(txtAddShipNotes.Text.Trim(), chkAddShipInGame.IsChecked == true);

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
        SetAddShipName("");
        cmbAddShipMfr.Text = "";
        txtAddShipScu.Text = "0";
        txtAddShipNotes.Text = "";
        chkAddShipInGame.IsChecked = false;
        Log($"[Ship] 追加: {name} ({scu} SCU){(IsInGameNotes(notes) ? " [ゲーム内購入]" : "")}");
    }

    // 所持船メモの「ゲーム内購入 (aUEC)」接頭辞。my_ships.notes の先頭に付けて由来を記録する (スキーマは変えない)
    private const string InGameNotesMarker = "[ゲーム内購入]";

    private static bool IsInGameNotes(string? notes)
        => !string.IsNullOrEmpty(notes) && notes.StartsWith(InGameNotesMarker, StringComparison.Ordinal);

    // 接頭辞を外したメモ本文 (接頭辞が無ければそのまま)
    private static string StripInGameMarker(string? notes)
        => IsInGameNotes(notes) ? notes!.Substring(InGameNotesMarker.Length).TrimStart() : (notes ?? "");

    // メモ本文に接頭辞を付け直す (既に付いていても二重には付けない。inGame=false なら外す)
    private static string ApplyInGameMarker(string? notes, bool inGame)
    {
        var body = StripInGameMarker(notes);
        if (!inGame) return body;
        return body.Length == 0 ? InGameNotesMarker : $"{InGameNotesMarker} {body}";
    }

    private void DeleteMyShip_Click(object sender, RoutedEventArgs e)
    {
        if (dgMyShips.SelectedItem is not MyShipRow ship) return;
        if (MessageBox.Show($"「{ship.Name}」を削除しますか？", "確認", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;

        _tradeService.DeleteMyShip(ship.Id);
        RefreshMyShips();
        Log($"[Ship] 削除: {ship.Name}");
    }

    // ワイプ (サーバーリセット) で無くなる船を選んでまとめて削除する。
    // 既定のチェックはゲーム内購入 (aUEC) の船だけ。pledge で持っている船はワイプで消えないので既定では外す
    private void ShipWipe_Click(object sender, RoutedEventArgs e)
    {
        var rows = (dgMyShips.ItemsSource as IEnumerable<MyShipRow>)?.ToList() ?? new List<MyShipRow>();
        if (rows.Count == 0)
        {
            MessageBox.Show("所持船が登録されていません。", "ワイプ");
            return;
        }

        var dlg = new ShipWipeDialog(rows.Select(r => new WipeShipRow
        {
            Id = r.Id,
            Selected = r.IsInGame,
            Name = r.Name,
            Manufacturer = r.Manufacturer,
            OriginDisplay = r.HangarOriginDisplay,
            HangarCountDisplay = r.HangarCountDisplay,
            IsPledge = r.HangarCount > 0,
        })) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var ids = dlg.SelectedShipIds;
        if (ids.Count == 0) return;

        var names = rows.Where(r => ids.Contains(r.Id)).Select(r => r.Name).ToList();
        var preview = string.Join("\n", names.Take(20));
        if (names.Count > 20) preview += $"\n… 他 {names.Count - 20} 隻";
        if (MessageBox.Show($"所持船 {ids.Count} 隻を削除しますか？\n\n{preview}",
                "ワイプ", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        foreach (var id in ids) _tradeService.DeleteMyShip(id);
        RefreshMyShips();
        Log($"[Ship] ワイプ: {ids.Count} 隻を削除");
        txtMyShipStatus.Text = $"所持船: {_tradeService.MyShips.Count} 隻 | ワイプで {ids.Count} 隻を削除";
    }

    private void MyShip_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (dgMyShips.SelectedItem is not MyShipRow ship) return;

        SetAddShipName(ship.Name);
        cmbAddShipMfr.Text = ship.Manufacturer;
        txtAddShipScu.Text = ship.Scu.ToString();
        // 「ゲーム内購入」は notes の接頭辞からチェックボックスへ復元し、メモ欄には本文だけを出す (保存時に ApplyInGameMarker で付け直す)
        chkAddShipInGame.IsChecked = IsInGameNotes(ship.Notes);
        txtAddShipNotes.Text = StripInGameMarker(ship.Notes);
        txtMyShipStatus.Text = $"編集中: {ship.Name} — 入力欄を変更して [追加] で新規 or 下の更新ボタンで上書き";

        // 装備サブタブの「現在の装備」もこの船に切り替える (タブは所持船のまま。装備タブを開けばこの船が出ている)
        var target = ResolveLoadoutChoiceForMyShip(ship);
        if (target != null)
        {
            cmbLoadoutShip.SelectedItem = target;   // SelectionChanged → RefreshLoadoutGrid (同じ選択なら発火しないので明示的にも更新)
            RefreshLoadoutGrid();
            txtMyShipStatus.Text += $" | 装備タブを「{ship.Name}」に切替";
        }
    }

    private void UpdateMyShip_Click(object sender, RoutedEventArgs e)
    {
        if (dgMyShips.SelectedItem is not MyShipRow ship) return;
        var name = cmbAddShipName.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;
        int.TryParse(txtAddShipScu.Text.Trim(), out var scu);
        var notes = ApplyInGameMarker(txtAddShipNotes.Text.Trim(), chkAddShipInGame.IsChecked == true);
        _tradeService.UpdateMyShip(ship.Id, name, cmbAddShipMfr.Text.Trim(), scu, notes);
        RefreshMyShips();
        Log($"[Ship] 更新: {name} ({scu} SCU){(IsInGameNotes(notes) ? " [ゲーム内購入]" : "")}");
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

        var buySystem = (cmbTradeBuySystem.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "全て";
        var sellSystem = (cmbTradeSellSystem.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "全て";

        var excludeOutposts = chkExcludeOutpost.IsChecked == true;
        var loadingDockOnly = chkLoadingDockOnly.IsChecked == true;
        var excludeLowStock = chkExcludeLowStock.IsChecked == true;

        var buyable = _tradeService.GetBuyableWithBestSell(terminal, sellSystem,
            excludeOutposts, loadingDockOnly, excludeLowStock, _selectedCommodities);
        var sellable = _tradeService.GetSellableWithBestBuy(terminal, buySystem,
            excludeOutposts, loadingDockOnly, excludeLowStock, _selectedCommodities);

        dgDetailBuy.ItemsSource = buyable.Select(r => new TradeDetailRow
        {
            Location = r.CommodityName,
            Price = $"{r.Price:N1}",
            Stock = r.Stock > 0 ? $"{r.Stock:N0}" : "-",
            Terminal = r.Terminal,
            BestLocation = r.HasCounterpart ? r.BestLocation : "-",
            Profit = r.HasCounterpart ? (r.ProfitPerScu >= 0 ? $"+{r.ProfitPerScu:N1}" : $"{r.ProfitPerScu:N1}") : "-",
            ProfitValue = r.ProfitPerScu,
            HasCounterpart = r.HasCounterpart,
        }).ToList();

        dgDetailSell.ItemsSource = sellable.Select(r => new TradeDetailRow
        {
            Location = r.CommodityName,
            Price = $"{r.Price:N1}",
            Stock = r.Stock > 0 ? $"{r.Stock:N0}" : "-",
            Terminal = r.Terminal,
            BestLocation = r.HasCounterpart ? r.BestLocation : "-",
            Profit = r.HasCounterpart ? (r.ProfitPerScu >= 0 ? $"+{r.ProfitPerScu:N1}" : $"{r.ProfitPerScu:N1}") : "-",
            ProfitValue = r.ProfitPerScu,
            HasCounterpart = r.HasCounterpart,
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

    // 起動時のバックグラウンド取得: 交易 (UEX) → 完了後に Ship Matrix / RSI ストア。
    // StartBackgroundTradeFetchAsync は内部で例外を握るので、交易が失敗しても Hangar 側は実行される
    private async Task RunBackgroundFetchesAsync()
    {
        await StartBackgroundTradeFetchAsync();
        await StartBackgroundHangarFetchAsync();
    }

    private void EnsureHangarProgressHooked()
    {
        if (_hangarProgressHooked) return;
        _hangarProgressHooked = true;
        _hangarService.OnProgress += msg => Log($"[Hangar] {msg}");
    }

    // Ship Matrix と RSI ストアをバックグラウンドで取得する (TTL 内ならキャッシュ)。
    // 失敗は Log のみ。UI スレッドで HTTP を待たない。
    // どちらかで実際に取得 (更新) があった場合、または LoadUpgradeData がまだ一度も正常終了していない場合に
    // アップグレード管理タブを再読込する (両方キャッシュ利用かつ StartBackgroundTradeFetchAsync 側の LoadUpgradeData で
    // 表示済みなら呼ばない。交易取得失敗で同メソッドが早期 return した場合はここで初回読込を行う)
    private async Task StartBackgroundHangarFetchAsync()
    {
        EnsureHangarProgressHooked();
        _hangarService.SetCacheDir(WorkDir);

        var matrixFetched = false;
        var storeFetched = false;

        try
        {
            var r = await Task.Run(() => _hangarService.RefreshShipMatrixAsync(false));
            matrixFetched = r.Fetched;
        }
        catch (Exception ex)
        {
            Log($"[Hangar] Ship Matrix 取得エラー: {ex}");
        }

        try
        {
            var r = await Task.Run(() => _hangarService.RefreshStoreAsync(false));
            storeFetched = r.Fetched;
        }
        catch (Exception ex)
        {
            Log($"[Hangar] ストア取得エラー: {ex}");
        }

        // ローナー機 (UEX)。所持船グリッドの「ローナー」列に出す。
        // 取得結果が変わったときだけ再読込対象にする (TTL 内のキャッシュ利用では再読込しない)
        var loanersFetched = false;
        try
        {
            loanersFetched = await Task.Run(async () =>
            {
                var before = _hangarService.LoadLoaners().Count;
                await _hangarService.RefreshLoanersAsync();
                return _hangarService.LoadLoaners().Count != before;
            });
        }
        catch (Exception ex)
        {
            Log($"[Hangar] ローナー取得エラー: {ex}");
        }

        if (!matrixFetched && !storeFetched && !loanersFetched && _upgradeDataLoadedOnce)
        {
            Log("[Hangar] Ship Matrix / ストアは共にキャッシュ利用のため再読込を省略");
            return;
        }

        try
        {
            Dispatcher.Invoke(() =>
            {
                _hangarService.InvalidateCaches();
                LoadUpgradeData();
            });
        }
        catch (Exception ex)
        {
            Log($"[Hangar] UI更新エラー: {ex}");
        }
    }

    // 「ストア情報を更新」: Ship Matrix と RSI ストアを強制再取得する
    private async void StoreRefresh_Click(object sender, RoutedEventArgs e)
    {
        EnsureHangarProgressHooked();
        btnStoreRefresh.IsEnabled = false;
        txtUpgradeStatus.Text = "ストア取得中…";
        txtUpgradeStatus.ToolTip = null;
        string result;
        try
        {
            _hangarService.SetCacheDir(WorkDir);
            var (count, _) = await Task.Run(async () =>
            {
                await _hangarService.RefreshShipMatrixAsync(true);
                return await _hangarService.RefreshStoreAsync(true);
            });
            result = $"ストア情報を更新しました: {count:N0} 件";
        }
        catch (Exception ex)
        {
            Log($"[Hangar] ストア取得エラー: {ex}");
            result = $"ストア取得エラー: {ex.Message}";
        }

        try
        {
            _hangarService.InvalidateCaches();
            LoadUpgradeData();
            txtUpgradeStatus.Text += $" | {result}";
        }
        finally
        {
            btnStoreRefresh.IsEnabled = true;
        }
    }

    // 「装備データを再抽出」: Data.p4k から船と装備 (item_index) だけを再抽出する (GameDataExtractor.RebuildEquipmentDataAsync)。
    // 初期装備 (デフォルトロードアウト) を正しく取り直すために使う。完了後は装備 DB (_hangarEquip) を開き直して再読込する
    private async void RebuildEquipment_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Data.p4k から船と装備のデータを再抽出します（数分）。続けますか？", "装備データを再抽出",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        btnRebuildEquipment.IsEnabled = false;
        SetUpgradeActionButtonsEnabled(false);   // 再抽出中は同期・再読み込み・ストア更新・リセットも止める (finally で戻す)
        txtUpgradeStatus.Text = "装備データを再抽出中...";
        txtUpgradeStatus.ToolTip = null;
        var progress = new Progress<string>(msg => txtUpgradeStatus.Text = msg);

        // 抽出先 (gamedata_cache.db) を開いている装備 DB 接続は先に閉じる (再抽出後に LoadUpgradeData が開き直す)
        _hangarEquip?.Dispose();
        _hangarEquip = null;

        string result;
        bool rebuilt = false;
        try
        {
            // InitGameDataExtractor と同じ引数で生成する。再抽出専用のインスタンスなので終了時に接続を閉じる
            var workDir = App.Config.WorkingDirectory;
            if (string.IsNullOrEmpty(workDir)) workDir = AppDomain.CurrentDomain.BaseDirectory;
            using var extractor = new GameDataExtractor(workDir);
            await Task.Run(() => extractor.RebuildEquipmentDataAsync(progress, CancellationToken.None));
            rebuilt = true;
            result = "装備データを再抽出しました";
            Log("[Hangar] 装備データを再抽出しました");
        }
        catch (Exception ex)
        {
            Log($"[Hangar] 装備データ再抽出エラー: {ex}");
            result = $"装備データ再抽出エラー: {ex.Message}";
            MessageBox.Show($"装備データの再抽出に失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        try
        {
            _hangarService.InvalidateCaches();
            if (rebuilt)
            {
                // 再抽出でポート構成 (PortKey) が変わった船に備え、存在しないポートキーの ship_loadouts 行を削除する
                var gamedataPath = Path.Combine(WorkDir, "gamedata_cache.db");
                if (File.Exists(gamedataPath))
                {
                    try
                    {
                        _hangarService.SetCacheDir(WorkDir);
                        using var equip = new EquipmentService(gamedataPath);
                        var pruned = _hangarService.PruneLoadouts(equip);
                        if (pruned > 0) Log($"[Hangar] 存在しないポートの装備設定 {pruned} 件を削除しました");
                    }
                    catch (Exception ex)
                    {
                        Log($"[Hangar] 装備設定 (ship_loadouts) の整理に失敗: {ex.Message}");
                    }
                }
            }
            LoadUpgradeData();   // _hangarEquip が null なので gamedata_cache.db を開き直す
            txtUpgradeStatus.Text += $" | {result}";
        }
        finally
        {
            btnRebuildEquipment.IsEnabled = true;
            SetUpgradeActionButtonsEnabled(true);
        }
    }

    // 装備データ再抽出中に止める操作ボタン (My Hangar と同期 ×2 / 再読み込み / ストア情報を更新 / シミュレーションをリセット) の有効・無効
    private void SetUpgradeActionButtonsEnabled(bool enabled)
    {
        btnSync.IsEnabled = enabled;
        btnSyncMyShips.IsEnabled = enabled;
        btnUpgradeReload.IsEnabled = enabled;
        btnStoreRefresh.IsEnabled = enabled;
        btnUpgradeReset.IsEnabled = enabled;
    }

    private void StoreFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (dgStore == null) return;   // XAML 読込中
        RefreshStoreGrid();
    }

    // 販売中の船グリッドにフィルタ (Warbond のみ / カテゴリ) を適用する
    private void RefreshStoreGrid()
    {
        IEnumerable<StoreRow> rows = _storeRows;
        if (chkStoreWarbondOnly.IsChecked == true)
            rows = rows.Where(r => r.IsWarbond);
        if (cmbStoreCategory.SelectedItem is string cat && cat != "すべて")
            rows = rows.Where(r => r.Category == cat);

        dgStore.ItemsSource = null;
        dgStore.ItemsSource = rows.ToList();
        txtStoreStatus.Text = $"最終取得: {_hangarService.StoreFetchedAt() ?? "未取得"} / {_storeSkus.Count} 件";
    }

    // カテゴリコンボを "すべて" + hangar_selectors.json のカテゴリ名で埋める (選択は維持)
    private void PopulateStoreCategoryCombo()
    {
        var items = new List<string> { "すべて" };
        items.AddRange(HangarService.LoadSelectors().StoreCategories.Values);
        var prev = cmbStoreCategory.SelectedItem as string;
        if (cmbStoreCategory.Items.Count == items.Count
            && cmbStoreCategory.Items.Cast<object>().Select(o => o as string).SequenceEqual(items))
            return;
        cmbStoreCategory.ItemsSource = items;
        cmbStoreCategory.SelectedItem = prev != null && items.Contains(prev) ? prev : "すべて";
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
                dgMyShips.ItemsSource = _tradeService.MyShips.Select(ToMyShipRow).ToList();
                txtMyShipStatus.Text = $"所持船: {_tradeService.MyShips.Count} 隻 | UEX船データ: {_tradeService.Ships.Count} 件";
                cmbAddShip.ItemsSource = _tradeService.Ships;
                cmbAddShip.DisplayMemberPath = "DisplayName";
                txtTradeStatus.Text = $"価格 {_tradeService.PriceCount:N0} 件 | 船 {_tradeService.Ships.Count} 件 | 所持船 {_tradeService.MyShips.Count} 隻 | 更新: {_tradeService.LastPriceUpdate:HH:mm}";
                LoadUpgradeData();
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
            dgMyShips.ItemsSource = _tradeService.MyShips.Select(ToMyShipRow).ToList();
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

public class UpgradeShipRow
{
    public string PledgeId { get; set; } = "";
    public string PledgeName { get; set; } = "";
    public string Name { get; set; } = "";              // 元の船名
    public string InstanceKey { get; set; } = "";       // HangarShipInstance.InstanceKey ("{pledge_id}|{ship_name}[#N]")。ヘッダ行・CCU 行は空
    public string CurrentName { get; set; } = "";       // 適用後の船名
    public string InsuranceDisplay { get; set; } = "";
    public string Focus { get; set; } = "";             // Ship Matrix: focus
    public string Type { get; set; } = "";              // Ship Matrix: type
    public string Status { get; set; } = "";            // Ship Matrix: production_status
    public bool IsUnresolved { get; set; }              // Ship Matrix で名寄せできなかった
    public bool IsDuplicate { get; set; }               // 名寄せ後の CurrentName が保有船の中で 2 機以上
    public HangarShipMark Mark { get; set; } = HangarShipMark.None;
    public List<string> AppliedCcuIds { get; set; } = new();   // 適用順
    public bool CanUpgrade { get; set; }
    public string OnSaleDisplay { get; set; } = "";     // RSI ストア: "販売中" / "Warbond 販売中" / ""
    public bool OnSaleWarbond { get; set; }             // Warbond 版が販売中
    public int SameTypeCount { get; set; }              // 正規化名が同じ保有船の数 (自分を含む)
    public string OriginDisplay { get; set; } = "";     // 由来 ("パック: ..." / "単品: ..." / "CCU 適用済 ← ..." / "VIP 特典")
    public string PledgeShipNames { get; set; } = "";   // 同 pledge の同梱機

    // Hangar (pledge) に一致しない所持船 (my_ships) を保有船グリッドに出した行。InstanceKey は "my|{my_ships.id}"。
    // CCU は pledge の船にしか使えないので CanUpgrade=false / Sell 不可。装備 (ship_loadouts) は設定できる
    public bool IsInGame { get; set; }                  // メモが "[ゲーム内購入]" で始まる所持船 (aUEC 購入)
    public bool IsManual { get; set; }                  // 上記以外の Hangar 未一致の所持船 (手入力・未同期)
    public bool IsNonPledge => IsInGame || IsManual;
    public int MyShipId { get; set; }                   // my_ships.id (IsNonPledge の行のみ。「装備」ボタンで "my|{id}" の船を開く)
    // ツールチップ。船行は TooltipFactory (HangarService.BuildShipTooltip) を初回アクセス時に評価してキャッシュする
    // (WPF の ToolTip バインドはツールチップ表示時に評価される)。ヘッダ行・CCU 行は文字列を直接セット
    private string? _tooltip;
    private Func<string>? _tooltipFactory;
    public Func<string>? TooltipFactory
    {
        get => _tooltipFactory;
        set { _tooltipFactory = value; _tooltip = null; }
    }
    public string Tooltip
    {
        get => _tooltip ??= _tooltipFactory?.Invoke() ?? "";
        set { _tooltip = value; _tooltipFactory = null; }
    }
    public string StoreUpgradeDisplay { get; set; } = "";   // "購入可 (N)" / "Warbond あり (N)" / ""
    public string UpgradableDisplay { get; set; } = "";     // CanUpgrade ? "○" : ""

    // Packages の展開表示: 同 pledge に 2 機以上ある pledge は「ヘッダ行 + 子行」
    public bool IsGroupHeader { get; set; }                 // pledge をまとめるヘッダ行 (船ではない)
    public string GroupPledgeId { get; set; } = "";         // ヘッダ／子行が属する pledge
    public bool IsExpanded { get; set; }                    // ヘッダ用: 子行を表示中か
    public bool IsChild { get; set; }                       // グループの子行
    public string DisplayName { get; set; } = "";           // 船名列の表示 (ヘッダ: "pledge名 (N機)" / 子: "　└ 船名" / 単独: 船名)
    public string ToggleLabel => IsGroupHeader ? (IsExpanded ? "−" : "+") : "";   // 非ヘッダは空 → ボタン非表示
    public bool MarkEnabled => !IsGroupHeader && !IsCcu && !IsNonPledge;   // 所持船由来の行は pledge ではないためマークの保存キーが無い
    public bool LoadoutEnabled => !IsGroupHeader && !IsCcu; // 「装備」ボタン: ヘッダ行・CCU 行は船ではないので無効

    // アップグレード権利 (CCU) を保有船グリッドに差し込んだ行。船行の直後 (使用可 / 使用中) または
    // 末尾の「使用不可のアップグレード権利」グループの子 (使用不可 / 使用不可 (売却))
    public bool IsCcu { get; set; }
    public string CcuPledgeId { get; set; } = "";           // CCU の pledge id (PledgeId にも同じ値を入れる: Sell は pledge 単位)
    public string CcuState { get; set; } = "";              // 使用可 / 使用中 / 売却予定 / 使用不可 / 使用不可 (売却)
    public UpgradeShipRow? CcuParent { get; set; }          // 適用先 (使用可) / 適用元 (使用中) の船行。使用不可は null
    public bool CcuIsLastApplied { get; set; }              // 使用中: 親の AppliedCcuIds の末尾か (末尾以外は Undo 不可)
    public bool IsCcuUnusable => IsCcu && CcuState.StartsWith("使用不可", StringComparison.Ordinal);
    public string PriceDisplay { get; set; } = "";          // 船行: pledge の melt 額 (子行は空) / CCU 行: 支払額
    public string DiscountDisplay { get; set; } = "";       // CCU 行: 割引率
    public string WarbondDisplay { get; set; } = "";        // CCU 行: "Warbond" / ""
    public string ToShipSaleDisplay { get; set; } = "";     // CCU 行: 先船の RSI ストア販売状況 ("Warbond" / "販売中" / "")

    // ボタンの有効状態 (船行・CCU 行で共通のプロパティ名にして XAML の IsEnabled にバインド)
    public bool UpgradeEnabled => IsCcu
        ? CcuState == "使用可" && CcuParent != null
        : !IsGroupHeader && !IsSold && CanUpgrade;
    public bool UndoEnabled => IsCcu
        ? CcuState == "使用中" && CcuIsLastApplied && CcuParent != null
        : HasApplied;

    // Sell (melt 予定)。pledge 単位 (R-02)
    public bool IsSold { get; set; }
    public string SellLabel => IsSold ? "Unsell" : "Sell";
    public bool SellEnabled { get; set; }                   // Meltable かつ pledge id あり (R-09)
    public bool Meltable { get; set; } = true;              // pledge の meltable
    public long PledgeValueCents { get; set; }              // pledge の melt 額
    public string? SellToolTip => IsNonPledge ? "pledge ではないため melt 対象外"
        : !Meltable ? "melt 不可"
        : IsCcu && CcuState == "使用中" ? "適用中のため Undo 後に売却できます"
        : null;

    public string SameTypeDisplay => SameTypeCount >= 2 ? $"×{SameTypeCount}" : "";
    public bool HasApplied => AppliedCcuIds.Count > 0;
    // CCU 行は状態を出す (使用中は先船も添える: "使用中 → <先船>"。CCU 行の CurrentName は ToShip)
    public string AppliedDisplay => IsCcu
        ? (CcuState == "使用中" ? $"使用中 → {CurrentName}" : CcuState)
        : HasApplied ? $"→ {CurrentName}" : "-";
    // 使える権利が無く、適用もしていない船 = 連鎖のない船 (ヘッダ行・CCU 行は対象外)
    public bool IsDeadEnd => !IsGroupHeader && !IsCcu && !HasApplied && !CanUpgrade;
    public string MarkDisplay => Mark switch
    {
        HangarShipMark.Keep => "要る",
        HangarShipMark.Drop => "要らない",
        HangarShipMark.Hold => "保留",
        _ => "",
    };
}

// 所持船 (交易用) グリッドの 1 行。MyShipEntry の写し + 同期済み保有船 (Hangar) との照合結果。
// 交易タブ (cmbTradeShip) は MyShipEntry をそのまま使うので、こちらは表示専用
public class MyShipRow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public int Scu { get; set; }
    public string Notes { get; set; } = "";
    public string AddedAt { get; set; } = "";
    public string DisplayName => Scu > 0 ? $"{Name} ({Scu} SCU)" : Name;

    public int HangarCount { get; set; }                    // 正規化名が一致する同期済み保有船の数 (0 = 未同期)
    public bool IsInGame { get; set; }                      // Hangar 未一致で、メモが "[ゲーム内購入]" で始まる (aUEC 購入の船。未同期とは表示しない)
    public string HangarCountDisplay => HangarCount > 0 ? $"×{HangarCount}" : IsInGame ? "" : "未同期";
    public string HangarOriginDisplay { get; set; } = "";   // 一致した保有船の由来を " / " 連結 (Hangar 未一致のゲーム内購入は "ゲーム内購入")
    public bool HangarCanUpgrade { get; set; }              // 一致した保有船のいずれかに使える権利がある
    public string HangarUpgradableDisplay { get; set; } = "";
    public string HangarSaleDisplay { get; set; } = "";     // "Warbond 販売中" / "販売中" / ""
    public string HangarStoreUpgradeDisplay { get; set; } = "";
    public string LoanerDisplay { get; set; } = "";   // その船で借りられる機体 (" / " 連結)。無ければ空
    // ツールチップ。TooltipFactory を初回アクセス時に評価してキャッシュする (UpgradeShipRow と同じ)
    private string? _tooltip;
    private Func<string>? _tooltipFactory;
    public Func<string>? TooltipFactory
    {
        get => _tooltipFactory;
        set { _tooltipFactory = value; _tooltip = null; }
    }
    public string Tooltip
    {
        get => _tooltip ??= _tooltipFactory?.Invoke() ?? "";
        set { _tooltip = value; _tooltipFactory = null; }
    }
    public override string ToString() => DisplayName;
}

// 装備サブタブ「現在の装備」の船セレクタ 1 件。ShipKey は ship_loadouts のキー
// ("{pledge_id}|{ship_name}" = アップグレード管理の保有船インスタンス / "my|{my_ships.id}" = 所持船のみの船)
public class LoadoutShipChoice
{
    public string ShipKey { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Manufacturer { get; set; }       // Ship Matrix のメーカー名 (ResolveShipRecordName 用)
    public string Display { get; set; } = "";
    public string? Record { get; set; }             // ships.record_name (初回参照時に解決してキャッシュ)
    public override string ToString() => Display;
}

// 装備グリッドの ComboBox の選択肢。IsDefault = 「(デフォルトに戻す)」
public class LoadoutChoice
{
    public bool IsDefault { get; set; }
    public string ItemRecord { get; set; } = "";
    public string ItemName { get; set; } = "";      // 購入先ツールチップ用のアイテム表示名 ("(デフォルトに戻す)" 等の擬似項目では空)
    public string Display { get; set; } = "";
    public override string ToString() => Display;
}

// 装備グリッドの 1 行 = ShipPortLoadout + 選択肢
public class LoadoutRow : ShipPortLoadout
{
    public List<LoadoutChoice> Candidates { get; set; } = new();
    public LoadoutChoice? SelectedChoice { get; set; }
    public bool HasCandidates => Candidates.Count > 1;      // 「(デフォルトに戻す)」以外に候補があるか
}

// 所持コンポーネント (dgMyComponents) の表示行。MyComponent を包み、Usable の変更を OnUsableChanged 経由で DB へ書き戻す
// (DataGridCheckBoxColumn の CellEditEnding / CurrentCellChanged には依存しない)
public class MyComponentRow : INotifyPropertyChanged
{
    public MyComponent Model { get; }
    public Action<MyComponentRow>? OnUsableChanged { get; set; }

    public MyComponentRow(MyComponent model) => Model = model;

    public int Id => Model.Id;
    public string ItemRecord => Model.ItemRecord;
    public string ItemName => Model.ItemName;
    public string ItemType => Model.ItemType;
    public string TypeDisplay => Model.TypeDisplay;
    public string SizeDisplay => Model.SizeDisplay;
    public string GradeDisplay => Model.GradeDisplay;
    public int Quantity => Model.Quantity;
    public string Notes => Model.Notes;
    public string AddedAt => Model.AddedAt;

    public bool Usable
    {
        get => Model.Usable;
        set
        {
            if (Model.Usable == value) return;
            Model.Usable = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Usable)));
            OnUsableChanged?.Invoke(this);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

// 所持コンポーネント追加パネルの候補 1 件 (EquipmentItem + 解決済み表示名)
public class CompCandidate
{
    public EquipmentItem Item { get; set; } = new();
    public string Name { get; set; } = "";
    public string Display { get; set; } = "";
    public override string ToString() => Display;
}

public class UpgradeCcuRow
{
    public string PledgeId { get; set; } = "";
    public string RouteDisplay { get; set; } = "";
    public string PriceDisplay { get; set; } = "";
    public string DiscountDisplay { get; set; } = "";
    public string WarbondDisplay { get; set; } = "";
    public string StateDisplay { get; set; } = "";      // 使用可 / 使用中 / 使用不可
    public bool IsUsed { get; set; }
    public bool IsUnusable { get; set; }
    public bool IsDuplicateTarget { get; set; }         // 名寄せ後の ToShip が既に保有型
    public string NoteDisplay => IsDuplicateTarget ? "先船は既に保有" : "";
    public string ToShipSaleDisplay { get; set; } = ""; // 先船の RSI ストア販売状況: "Warbond" / "販売中" / ""
}

// CCU プランナーの提案 1 行 (適用 / melt / 使用不可)
public class PlanRow
{
    public string Kind { get; set; } = "";
    public string Detail { get; set; } = "";
    public string AmountDisplay { get; set; } = "";
    public string Reason { get; set; } = "";
}

// melt シミュレーションの pledge 1 行。Selected は DataGrid の two-way 更新で書き戻され、
// PropertyChanged で MainWindow._soldPledgeIds と双方向に同期する
public class MeltPledgeRow : INotifyPropertyChanged
{
    private bool _selected;
    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public string PledgeId { get; set; } = "";
    public string Name { get; set; } = "";
    public string ValueDisplay { get; set; } = "";
    public string InsuranceDisplay { get; set; } = "";
    public string Ships { get; set; } = "";
    public bool Meltable { get; set; } = true;

    // チェック可否。Meltable かつ適用中 (AppliedCcuIds に含まれる) の CCU pledge でない。RecomputeUpgradeSim が更新する
    private bool _canSelect = true;
    public bool CanSelect
    {
        get => _canSelect;
        set
        {
            if (_canSelect == value) return;
            _canSelect = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanSelect)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectToolTip)));
        }
    }
    public string? SelectToolTip => Meltable && !CanSelect ? "適用中のため Undo 後に売却できます" : null;
}

// 販売中の船 (RSI ストア) グリッドの 1 行
public class StoreRow
{
    public string Category { get; set; } = "";
    public string Name { get; set; } = "";
    public string NativePriceDisplay { get; set; } = "";
    public bool IsWarbond { get; set; }
    public string WarbondDisplay => IsWarbond ? "Warbond" : "";
    public string StockLevel { get; set; } = "";
    public string OwnedDisplay { get; set; } = "";      // 保有船に同名があれば "保有"
}

public class TradeDetailRow
{
    public string Location { get; set; } = "";
    public string Price { get; set; } = "";
    public string Stock { get; set; } = "";
    public string Terminal { get; set; } = "";
    public string BestLocation { get; set; } = "";
    public string Profit { get; set; } = "";
    public double ProfitValue { get; set; }
    public bool HasCounterpart { get; set; }
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
