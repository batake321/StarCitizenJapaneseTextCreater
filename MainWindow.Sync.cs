using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace StarCitizenJapaneseTextCreater;

// PC 間の同期 (SyncService) の呼び出し口。いつ書き出す・取り込むか:
//   - アプリ起動 (Window_Loaded → InitSync): 取り込み → 書き出し
//   - ゲーム起動 (GameLogPanel.GameLogReset: Game.log が最初から読み直しになったとき): 取り込み
//   - ゲーム中 (Game.log が 10 分以内に更新されている間): 10 分ごとに書き出し
//   - ゲーム終了: StarCitizen のプロセスがゲーム起動の後に一度でも見つかっていて見つからなくなったとき、
//     プロセスが一度も見つからない環境では Game.log の更新が 5 分止まったとき → 書き出し (1 回)
//   - アプリ終了 (Window_Closing → SyncExportOnClosing): 書き出し (最大 10 秒待つ)
// 共有フォルダへは裏のスレッド (SyncService の Task.Run) だけで触る。失敗はログだけ (設定タブの手動のボタンのときだけ MessageBox)。
// 取り込みの間は通常のインポート (ImportDb_Click) と同じく売り場・価格の取得を止め、終わったら同じ後処理をする
public partial class MainWindow
{
    private static readonly TimeSpan SyncPeriodicInterval = TimeSpan.FromMinutes(10);   // ゲーム中の書き出しの間隔 (Game.log の更新がこの間隔の中にある間)
    private static readonly TimeSpan SyncLogIdleEnd = TimeSpan.FromMinutes(5);          // プロセスが見つからない環境での、ゲーム終了と見る Game.log の無更新の時間
    private const string GameProcessName = "StarCitizen";

    private DispatcherTimer? _syncTimer;                                 // 1 分ごとの見回り
    private DateTime _syncLastExportAttemptUtc = DateTime.MinValue;      // 最後に書き出しを試みた時刻 (10 分ごとの間隔に使う)
    private bool _syncGameSession;                                       // ゲーム起動 (reset) の後で、ゲーム終了の書き出しをまだしていない
    private bool _syncGameProcessSeen;                                   // そのゲーム起動の後に StarCitizen のプロセスが一度でも見つかった
    private bool _syncImportRunning;                                     // 取り込み (後処理まで) の最中
    private string _syncLastExportText = "まだありません";
    private string _syncLastImportText = "まだありません";

    private static string SyncMachine => Environment.MachineName;
    private static string SyncAppVersion => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "";
    private string SyncGameLogPath => Path.Combine(App.Config.GamePath ?? "", "Game.log");

    // Window_Loaded から
    private void InitSync()
    {
        txtSyncFolder.Text = App.Config.SyncFolder;
        UpdateSyncStatusText();
        gameLogPanel.GameLogReset += OnGameLogResetForSync;
        _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _syncTimer.Tick += (_, _) => SyncTimer_Tick();
        _syncTimer.Start();
        _ = SyncOnStartupAsync();
    }

    private async Task SyncOnStartupAsync()
    {
        if (string.IsNullOrWhiteSpace(App.Config.SyncFolder)) return;
        await SyncImportAsync(manual: false, trigger: "起動");
        await SyncExportAsync(manual: false, trigger: "起動");
    }

    // ゲーム起動 (Game.log が最初から読み直しになった)
    private void OnGameLogResetForSync()
    {
        _syncGameSession = true;
        _syncGameProcessSeen = false;
        if (string.IsNullOrWhiteSpace(App.Config.SyncFolder)) return;
        _ = SyncImportAsync(manual: false, trigger: "ゲーム起動");
    }

    private void SyncTimer_Tick()
    {
        if (string.IsNullOrWhiteSpace(App.Config.SyncFolder)) return;
        var now = DateTime.UtcNow;
        DateTime? logWrite = null;
        try
        {
            var logPath = SyncGameLogPath;
            if (File.Exists(logPath)) logWrite = File.GetLastWriteTimeUtc(logPath);
        }
        catch { }

        if (_syncGameSession)
        {
            var found = IsGameProcessRunning();
            if (found) _syncGameProcessSeen = true;
            else if (_syncGameProcessSeen)
            {
                _syncGameSession = false;
                _ = SyncExportAsync(manual: false, trigger: "ゲーム終了");
                return;
            }
            else if (logWrite is { } w && now - w > SyncLogIdleEnd)
            {
                _syncGameSession = false;
                _ = SyncExportAsync(manual: false, trigger: "ゲーム終了 (Game.log の更新が 5 分止まった)");
                return;
            }
        }
        var inGame = logWrite is { } lw && now - lw <= SyncPeriodicInterval;
        if (inGame && now - _syncLastExportAttemptUtc >= SyncPeriodicInterval)
            _ = SyncExportAsync(manual: false, trigger: "ゲーム中");
    }

    private static bool IsGameProcessRunning()
    {
        try
        {
            var procs = Process.GetProcessesByName(GameProcessName);
            try { return procs.Length > 0; }
            finally { foreach (var p in procs) p.Dispose(); }
        }
        catch { return false; }
    }

    // 書き出し。folderOverride は手動のボタン (保存前の欄の値で動かす) 用
    private async Task<SyncService.ExportResult?> SyncExportAsync(bool manual, string trigger, string? folderOverride = null)
    {
        var folder = (folderOverride ?? App.Config.SyncFolder ?? "").Trim();
        if (folder.Length == 0)
        {
            if (manual) MessageBox.Show("共有フォルダが設定されていません。", "PC 間の同期", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        _syncLastExportAttemptUtc = DateTime.UtcNow;
        try
        {
            var r = await SyncService.ExportAsync(folder, WorkDir, SyncMachine, SyncAppVersion, s => Log($"[同期] {s}"));
            var kb = r.ZipBytes / 1024.0;
            Log($"[同期] 書き出し ({trigger}): {kb:N0} KB → {r.ZipPath}");
            _syncLastExportText = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} 成功 ({trigger}、{kb:N0} KB)";
            UpdateSyncStatusText();
            if (manual) MessageBox.Show($"書き出しました ({kb:N0} KB)。\n{r.ZipPath}", "PC 間の同期");
            return r;
        }
        catch (Exception ex)
        {
            Log($"[同期] 書き出し ({trigger}) に失敗: {ex.Message}");
            _syncLastExportText = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} 失敗 ({trigger}): {ex.Message}";
            UpdateSyncStatusText();
            if (manual) MessageBox.Show($"書き出しに失敗しました: {ex.Message}", "PC 間の同期", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
    }

    // アプリ終了時の書き出し (Window_Closing から。UI のスレッドで共有フォルダに触らず、裏の Task を最大 10 秒待つ)
    private void SyncExportOnClosing()
    {
        _syncTimer?.Stop();
        var folder = (App.Config.SyncFolder ?? "").Trim();
        if (folder.Length == 0) return;
        var workDir = WorkDir;
        void LogFile(string s) => App.AppendStartupLog(workDir, $"[同期] {s}");
        try
        {
            var task = Task.Run(() => SyncService.Export(folder, workDir, SyncMachine, SyncAppVersion, LogFile));
            if (task.Wait(TimeSpan.FromSeconds(10)))
                LogFile($"書き出し (終了): {task.Result.ZipBytes / 1024.0:N0} KB → {task.Result.ZipPath}");
            else
                LogFile("書き出し (終了) が 10 秒で終わらなかったので待つのをやめました");
        }
        catch (AggregateException ex) { LogFile($"書き出し (終了) に失敗: {ex.InnerException?.Message ?? ex.Message}"); }
        catch (Exception ex) { LogFile($"書き出し (終了) に失敗: {ex.Message}"); }
    }

    // 取り込み。新しい書き出しが無ければ DB に触らずに終わる。あれば ImportDb_Click と同じく、売り場・価格の取得を止めて取り込み、同じ後処理をする
    private async Task SyncImportAsync(bool manual, string trigger, string? folderOverride = null)
    {
        var folder = (folderOverride ?? App.Config.SyncFolder ?? "").Trim();
        if (folder.Length == 0)
        {
            if (manual) MessageBox.Show("共有フォルダが設定されていません。", "PC 間の同期", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_syncImportRunning)
        {
            if (manual) MessageBox.Show("取り込みの最中です。", "PC 間の同期");
            return;
        }
        _syncImportRunning = true;
        try
        {
            Action<string> log = s => Log($"[同期] {s}");
            if (!Directory.Exists(folder))
                throw new DirectoryNotFoundException($"共有フォルダが見つかりません: {folder}");
            var candidates = await Task.Run(() => SyncService.ListCandidates(folder, WorkDir, SyncMachine, log));
            if (candidates.Count == 0)
            {
                Log($"[同期] 取り込み ({trigger}): 新しい書き出しはありません");
                _syncLastImportText = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} 新しい書き出しはありません ({trigger})";
                UpdateSyncStatusText();
                if (manual) MessageBox.Show("ほかの PC の新しい書き出しはありません。", "PC 間の同期");
                return;
            }

            // 翻訳・用語集が替わるかもしれないので、ImportDb_Click と同じく装備タブの読み込み口を閉じてから取り込み、後で作り直す
            _hangarEquip?.Dispose();
            _hangarEquip = null;
            lock (_priceRefreshTasks) _importsRunning++;
            (string DbPath, bool AfterRebuild)? deferred = null;
            bool lastImport = false;
            SyncService.ImportResult? result = null;
            Exception? error = null;
            try
            {
                await GameDataExtractor.PauseShopPriceFetchAsync();
                result = await SyncService.ImportAsync(folder, WorkDir, SyncMachine, log);
            }
            catch (Exception ex) { error = ex; }
            finally
            {
                lock (_priceRefreshTasks)
                {
                    _importsRunning--;
                    if (_importsRunning == 0)
                    {
                        lastImport = true;
                        deferred = _deferredShopPriceRequest;
                        _deferredShopPriceRequest = null;
                    }
                }
                ReopenHangarEquip();
                if (lastImport) RunDeferredItemNameRefresh();
                InvalidateImportMarks();
                RefreshMyGear();
                gameLogPanel?.RefreshPurchaseButtons();
                orePanel?.Reload();
                if (!lastImport) { }
                else if (deferred is { } d)
                {
                    if (d.AfterRebuild) OnShopPriceFetchRequested(d.DbPath);
                    else OnShopPriceFetchResumeRequested(d.DbPath);
                }
                else if (!_indexBuildRunning && !GameDataExtractor.IsIndexBuildRunning)
                    OnShopPriceFetchResumeRequested(IndexDbPath);
            }
            UpdateDbPathDisplay();
            RefreshEditor();
            LoadGlossary();
            // 所持船・所持部品・装備も替わったかもしれないので読み直す
            try { RefreshMyShips(); } catch (Exception ex) { Log($"[同期] 所持船の読み直しに失敗: {ex.Message}"); }
            try { RefreshMyComponents(); } catch (Exception ex) { Log($"[同期] 所持部品の読み直しに失敗: {ex.Message}"); }
            try { RefreshLoadoutGrid(); } catch (Exception ex) { Log($"[同期] 装備の読み直しに失敗: {ex.Message}"); }

            if (error != null)
            {
                Log($"[同期] 取り込み ({trigger}) に失敗: {error}");
                _syncLastImportText = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} 失敗 ({trigger}): {error.Message}";
                UpdateSyncStatusText();
                if (manual) MessageBox.Show($"取り込みに失敗しました: {error.Message}", "PC 間の同期", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            var r = result!;
            var peers = string.Join(", ", r.Imported.Select(p => p.Machine));
            var summary = r.Imported.Count == 0 ? "取り込んだものはありません" : $"{peers} から取り込みました";
            if (r.ItemNamesRefreshed) summary += " (翻訳の個人行か用語集が変わったので品目名を今の訳に合わせました)";
            if (r.Errors.Count > 0) summary += $"。失敗: {string.Join(" / ", r.Errors)}";
            Log($"[同期] 取り込み ({trigger}): {summary}");
            _syncLastImportText = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {(r.Errors.Count > 0 ? "一部失敗" : "成功")} ({trigger}): {summary}";
            UpdateSyncStatusText();
            if (manual) MessageBox.Show(summary, "PC 間の同期", MessageBoxButton.OK, r.Errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log($"[同期] 取り込み ({trigger}) に失敗: {ex.Message}");
            _syncLastImportText = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} 失敗 ({trigger}): {ex.Message}";
            UpdateSyncStatusText();
            if (manual) MessageBox.Show($"取り込みに失敗しました: {ex.Message}", "PC 間の同期", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _syncImportRunning = false; }
    }

    private void UpdateSyncStatusText()
    {
        if (txtSyncStatus == null) return;
        txtSyncStatus.Text = $"この PC: {SyncMachine}\n最後の書き出し: {_syncLastExportText}\n最後の取り込み: {_syncLastImportText}";
    }

    // 設定タブ「PC 間の同期」のボタン
    private void SyncBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "同期に使う共有フォルダを選択" };
        var current = txtSyncFolder.Text.Trim();
        if (current.Length > 0 && Directory.Exists(current)) dlg.InitialDirectory = current;
        if (dlg.ShowDialog(this) == true) txtSyncFolder.Text = dlg.FolderName;
    }

    private async void SyncExportNow_Click(object sender, RoutedEventArgs e) =>
        await SyncExportAsync(manual: true, trigger: "手動", folderOverride: txtSyncFolder.Text);

    private async void SyncImportNow_Click(object sender, RoutedEventArgs e) =>
        await SyncImportAsync(manual: true, trigger: "手動", folderOverride: txtSyncFolder.Text);
}
