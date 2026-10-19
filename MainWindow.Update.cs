using System.Windows;
using System.Windows.Threading;

namespace StarCitizenJapaneseTextCreater;

// 自動更新 (AppUpdater / UpdateWindow)。
//   確かめるとき: 起動したとき (画面が出て 20 秒後) と、毎日 App.Config.AutoUpdateTime (既定 08:00) を過ぎたとき (1 日 1 回)。どちらも自動更新が入のときだけ。
//     設定タブの［更新を確認］は、入切にかかわらずいつでも
//   新しい版があれば、ダウンロードの前に UpdateWindow (最前面・フォーカスを奪わない) で聞く。
//     ［終了して更新］: 差分を取って確かめ、終わったら閉じる処理 (RunExitAsync) を通してアプリを閉じ、App の Exit で入れ替えを始める (終わったら起動し直す)
//     ［次回更新時に知らせる］(× も): 何もしない。次に確かめるとき (起動・毎日の時刻) にまた聞く
//     ［今は更新しない］: この版は見送る (自動の確かめでは、この版では聞かない)
//     ［後で手動で更新する］: 自動更新を切にする (設定を保存)。以後は［更新を確認］で
//   失敗 (ネットワーク・ハッシュの不一致・書き込めない) は、ログ (［ログ］・app_log.txt) と設定タブの状態の欄にだけ出す (窓は出さない)
public partial class MainWindow
{
    private AppUpdater? _updater;
    private DispatcherTimer? _updateTimer;
    private UpdateWindow? _updateWindow;
    private bool _updateChecking;

    private void InitAutoUpdate()
    {
        chkAutoUpdate.IsChecked = App.Config.AutoUpdateEnabled;
        txtAutoUpdateTime.Text = string.IsNullOrWhiteSpace(App.Config.AutoUpdateTime) ? "08:00" : App.Config.AutoUpdateTime;
        try { _updater = new AppUpdater(s => Log($"[更新] {s}")); }
        catch (Exception ex) { Log($"[更新] 自動更新を使えません: {ex.Message}"); return; }

        // 前回の入れ替えの結果 (まだ知らせていないもの)
        if (_updater.TakeUnreportedResult() is { } r)
        {
            Log($"[更新] {r.Message} ({r.FinishedAt})");
            txtUpdateStatus.Text = r.Message;
            // 戻せず、手で直す必要があるときは窓で知らせる (控えの場所と、手で入れ直すこと)
            if (r.Manual) Dispatcher.BeginInvoke(() => MessageBox.Show(this, r.Message, "自動更新", MessageBoxButton.OK, MessageBoxImage.Warning));
        }
        else txtUpdateStatus.Text = $"今の版: v{_updater.CurrentVersion.ToString(3)}";

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _updateTimer.Tick += (_, _) => UpdateTimer_Tick();
        _updateTimer.Start();
        // 起動したときの確かめ (画面が出てから少し置く)。この日の毎日の時刻をもう過ぎていれば、その分も済んだことにする
        var startup = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        startup.Tick += async (_, _) =>
        {
            startup.Stop();
            if (!App.Config.AutoUpdateEnabled || _exiting) return;
            if (TryDailyTime(out var at) && DateTime.Now.TimeOfDay >= at) MarkDailyDone();
            await CheckForUpdateAsync(auto: true);
        };
        startup.Start();
    }

    private static bool TryDailyTime(out TimeSpan at) =>
        TimeSpan.TryParseExact((App.Config.AutoUpdateTime ?? "").Trim(), @"h\:mm", null, out at) && at >= TimeSpan.Zero && at < TimeSpan.FromDays(1);

    private void MarkDailyDone()
    {
        if (_updater == null) return;
        var st = _updater.LoadState();
        st.LastDailyDate = DateTime.Now.ToString("yyyy-MM-dd");
        _updater.SaveState(st);
    }

    private async void UpdateTimer_Tick()
    {
        if (_updater == null || _exiting || !App.Config.AutoUpdateEnabled || !TryDailyTime(out var at)) return;
        if (DateTime.Now.TimeOfDay < at) return;
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (_updater.LoadState().LastDailyDate == today) return;
        MarkDailyDone();
        await CheckForUpdateAsync(auto: true);
    }

    private async void UpdateCheck_Click(object sender, RoutedEventArgs e) => await CheckForUpdateAsync(auto: false);

    private async Task CheckForUpdateAsync(bool auto)
    {
        if (_updater == null || _updateChecking || _exiting) return;
        if (_updateWindow != null) { if (!auto) _updateWindow.Show(); return; }   // もう聞いている
        _updateChecking = true;
        try
        {
            if (!auto) txtUpdateStatus.Text = "更新を確かめています…";
            var updater = _updater;
            var r = await Task.Run(() => updater.CheckAsync(auto));
            if (_exiting) return;
            switch (r.Kind)
            {
                case AppUpdater.CheckKind.UpToDate:
                    txtUpdateStatus.Text = r.Message;
                    if (!auto) Log($"[更新] {r.Message}");
                    break;
                case AppUpdater.CheckKind.Skipped:
                case AppUpdater.CheckKind.Failed:
                    // 失敗は静かに (窓を出さない)。ログは AppUpdater が出している
                    txtUpdateStatus.Text = r.Message;
                    break;
                case AppUpdater.CheckKind.ManualNeeded:
                    txtUpdateStatus.Text = r.Message;
                    ShowUpdateWindow(r, manualOnly: !auto);
                    break;
                case AppUpdater.CheckKind.Available:
                    txtUpdateStatus.Text = r.Message;
                    ShowUpdateWindow(r, manualOnly: !auto);
                    break;
            }
        }
        catch (Exception ex)
        {
            txtUpdateStatus.Text = $"更新の確かめに失敗しました: {ex.Message}";
            Log($"[更新] 更新の確かめに失敗しました: {ex.Message}");
        }
        finally { _updateChecking = false; }
    }

    private void ShowUpdateWindow(AppUpdater.CheckResult r, bool manualOnly)
    {
        if (_updater == null) return;
        var updater = _updater;
        var plan = r.Plan;
        Func<IProgress<(long Done, long Total)>, CancellationToken, Task>? download = plan == null ? null
            : (progress, ct) => Task.Run(() => updater.DownloadAsync(plan, progress, ct), ct);
        var w = new UpdateWindow(r, updater.CurrentVersion, download, manualOnly);
        _updateWindow = w;
        w.Closed += (_, _) => { if (ReferenceEquals(_updateWindow, w)) _updateWindow = null; };
        w.Chosen += choice => OnUpdateChosen(r, choice, w);
        w.Show();   // ShowActivated = false なので、フォーカスは奪わない
    }

    private void OnUpdateChosen(AppUpdater.CheckResult r, UpdateWindow.UpdateChoice choice, UpdateWindow w)
    {
        if (_updater == null) return;
        switch (choice)
        {
            case UpdateWindow.UpdateChoice.Update when r.Plan != null:
                // 取り終えた: 閉じる処理を通して閉じ、App の Exit で入れ替えを始める (終わったら起動し直す)
                Log($"[更新] v{r.Version} をダウンロードしました。アプリを閉じて入れ替えます");
                AppUpdater.PendingOnExit = (_updater, r.Plan);
                Dispatcher.BeginInvoke(() =>
                {
                    try { w.Close(); } catch { }
                    Close();
                });
                break;
            case UpdateWindow.UpdateChoice.Skip when r.Kind == AppUpdater.CheckKind.Available || r.Kind == AppUpdater.CheckKind.ManualNeeded:
            {
                // この版は見送る (手で入れる必要がある版の知らせも、同じ版ではもう出さない)
                var st = _updater.LoadState();
                st.SkippedVersion = r.Version;
                _updater.SaveState(st);
                if (r.Kind == AppUpdater.CheckKind.Available) txtUpdateStatus.Text = string.Format(UpdateTexts.Skipped, r.Version);
                break;
            }
            case UpdateWindow.UpdateChoice.Manual:
                App.Config.AutoUpdateEnabled = false;
                chkAutoUpdate.IsChecked = false;
                SaveConfigToFile();
                txtUpdateStatus.Text = UpdateTexts.ManualOff;
                Dispatcher.BeginInvoke(() => MessageBox.Show(this, UpdateTexts.ManualOff, "自動更新", MessageBoxButton.OK, MessageBoxImage.Information));
                break;
            case UpdateWindow.UpdateChoice.NextCheck:
                txtUpdateStatus.Text = UpdateTexts.NextCheck;
                break;
        }
    }

    // 設定の保存 (SaveSettings_Click から)。時刻が読めなければ前のまま
    private void SaveAutoUpdateSettings()
    {
        App.Config.AutoUpdateEnabled = chkAutoUpdate.IsChecked == true;
        var t = txtAutoUpdateTime.Text.Trim();
        if (TimeSpan.TryParseExact(t, @"h\:mm", null, out var ts) && ts >= TimeSpan.Zero && ts < TimeSpan.FromDays(1))
            App.Config.AutoUpdateTime = $"{ts.Hours:00}:{ts.Minutes:00}";
        txtAutoUpdateTime.Text = App.Config.AutoUpdateTime;
    }
}
