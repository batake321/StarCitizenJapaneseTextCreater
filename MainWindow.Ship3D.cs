using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace StarCitizenJapaneseTextCreater;

// 設定タブの「3D モデル」の欄 (Ship3DExports): 置き場所 (参照…)・置き場所のドライブの容量・最大容量・今の使用量 (船とステーションの地図の合計)・［船舶・車両をまとめて取り込む］・
// ［マップをまとめて取り込む］(StationMapExports)
public partial class MainWindow
{
    private const long Ship3DMB = 1024 * 1024;

    // 2 つのまとめて取り込むのボタンの ToolTip の元の文 (XAML のもの。後ろに今の Data.p4k で取り込んだ数・取り込んでいない数を足して出す)
    private string _ship3DBatchTip = "";
    private string _stationBatchTip = "";

    // Window_Loaded から
    private void InitShip3DSettings()
    {
        _ship3DBatchTip = btnShip3DBatch.ToolTip as string ?? "";
        _stationBatchTip = btnStationBatch.ToolTip as string ?? "";
        txtShip3DFolder.Text = Ship3DExports.Folder;
        txtShip3DMaxMB.Text = Ship3DExports.MaxMb.ToString();
        UpdateShip3DDriveText();
        UpdateShip3DUsageText();
        // 「全部取り込み済み」の数え直しは、知らせが短い間に続けて来たら 1 回にまとめる (最後の知らせから 300 ms 後に、UI スレッドの外で数える)
        _batchCountTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _batchCountTimer.Tick += (_, _) => { _batchCountTimer.Stop(); _ = RecountBatchTargetsAsync(); };
        _lastShipBatchActive = Ship3DExports.Instance.ShipBatchActive;
        UpdateShip3DBatchButton();
        UpdateStationBatchButton();
        RequestBatchRecount();
        // CPU 使用率が 90% をまたいだ・書き出し・照合・キャッシュを書いた / 消したとき
        Ship3DExports.Instance.StateChanged += () => { UpdateShip3DUsageText(); UpdateShip3DBatchButton(); UpdateStationBatchButton(); RequestBatchRecount(); };
        Ship3DExports.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Ship3DExports.BatchActive))
            {
                UpdateShip3DBatchButton();
                UpdateShip3DUsageText();
                // BatchActive の知らせはステーションのまとめての段階の文が変わるたびにも来る (NotifyStationBatchChanged) ので、
                // 船のまとめてが始まった・終わったときだけ数え直す
                var active = Ship3DExports.Instance.ShipBatchActive;
                if (active != _lastShipBatchActive) { _lastShipBatchActive = active; RequestBatchRecount(); }
            }
        };
        // ステーションの地図: 取り込み・照合・地図を書いた / 消した・まとめて取り込むの段階が変わったとき
        StationMapExports.Instance.StateChanged += () => { UpdateShip3DUsageText(); UpdateStationBatchButton(); RequestBatchRecount(); };
        StationMapExports.Instance.PropertyChanged += (_, e) =>
        {
            // 段階の文 (BatchText) が変わっただけのときは数え直さない
            if (e.PropertyName is nameof(StationMapExports.BatchActive) or nameof(StationMapExports.BatchText)) UpdateStationBatchButton();
            if (e.PropertyName == nameof(StationMapExports.BatchActive)) RequestBatchRecount();
        };
        // Data.p4k の印が変わったとき (設定タブを開いたままパッチが入ったとき等)
        Ship3DExports.Instance.P4kStampChanged += RequestBatchRecount;
        // 設定タブを開いたとき: 全部取り込み済みかを出し直す (パッチで Data.p4k が変わっていれば押せるようになる)
        tabMain.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, tabMain) && ReferenceEquals(tabMain.SelectedItem, tabSettings)) RequestBatchRecount();
        };
    }

    // まとめて取り込むの対象の記録 (BatchTargetRecord) から数えた、今の Data.p4k での状態。
    //   NoGame: ゲームの Data.p4k が見つからない。Counts: (取り込んだ数, 取り込んでいない数)。記録が無い・記録の Data.p4k の印が今と違う (パッチ後にまだまとめて取り込んでいない) ときは null
    private sealed record BatchCountState(bool NoGame, (int Done, int NotDone)? Counts);

    // 最後に数えた結果 (まだ数えていなければ null)
    private BatchCountState? _shipBatchCounts;
    private BatchCountState? _stationBatchCounts;
    private System.Windows.Threading.DispatcherTimer? _batchCountTimer;
    private bool _batchCountRunning;
    private bool _batchCountAgain;
    private bool _lastShipBatchActive;

    // 数え直しを頼む (300 ms の間に来た頼みは 1 回にまとめる)
    private void RequestBatchRecount()
    {
        if (_batchCountTimer == null) return;
        _batchCountTimer.Stop();
        _batchCountTimer.Start();
    }

    // 数え直す: 記録・横の記録・地図の JSON の頭を読むのは UI スレッドの外、照合中・後で消すもの (HeldBack) を除くのと結果をボタンに出すのは UI スレッドで。
    // 数えている間に頼みが来たら、終わってからもう 1 回数える
    private async Task RecountBatchTargetsAsync()
    {
        if (_batchCountRunning) { _batchCountAgain = true; return; }
        _batchCountRunning = true;
        try
        {
            do
            {
                _batchCountAgain = false;
                var (ships, maps) = await Task.Run(() =>
                {
                    var stamp = Ship3DExports.CurrentStamp();
                    return (CountOnDisk(BatchTargetRecord.Ships, stamp, Ship3DExports.IsFreshOnDisk),
                            CountOnDisk(BatchTargetRecord.Maps, stamp, StationMapExports.IsFreshOnDisk));
                });
                _shipBatchCounts = FinishCount(ships, Ship3DExports.Instance.HeldBack);
                _stationBatchCounts = FinishCount(maps, StationMapExports.Instance.HeldBack);
                UpdateShip3DBatchButton();
                UpdateStationBatchButton();
            } while (_batchCountAgain);
        }
        catch (Exception ex) { App.AppendStartupLog(App.Config.WorkingDirectory, $"[3D モデル] まとめて取り込むの取り込み済みの数を数えられませんでした: {ex.Message}"); }
        finally { _batchCountRunning = false; }
    }

    // UI スレッドの外: 記録を読み、印が今と同じなら、ファイルの上で最新 (IsFreshOnDisk) の record と全体の数
    private static (bool NoGame, List<string>? Fresh, int Total) CountOnDisk(string fileName, Ship3DExports.P4kStamp? stamp, Func<string, Ship3DExports.P4kStamp, bool> freshOnDisk)
    {
        if (stamp == null) return (true, null, 0);
        var rec = BatchTargetRecord.Read(fileName);
        if (rec == null || rec.P4k != stamp.Key) return (false, null, 0);
        return (false, rec.Records.Where(r => freshOnDisk(r, stamp)).ToList(), rec.Records.Count);
    }

    // UI スレッド: 照合中・後で消すもの (StateOf では最新でない) を除いて、取り込んだ数・取り込んでいない数にする
    private static BatchCountState FinishCount((bool NoGame, List<string>? Fresh, int Total) c, Func<string, bool> heldBack)
    {
        if (c.NoGame) return new BatchCountState(true, null);
        if (c.Fresh == null) return new BatchCountState(false, null);
        var done = c.Fresh.Count(r => !heldBack(r));
        return new BatchCountState(false, (done, c.Total - done));
    }

    // 全部取り込み済みか (対象が 1 つ以上あり、取り込んでいないものが無い)
    private static bool BatchAllDone(BatchCountState? s) => s?.Counts is { NotDone: 0, Done: > 0 };

    // ToolTip の後ろに足す文 (unit は「隻」「か所」。まだ数えていなければ足さない)
    private static string BatchRecordTip(BatchCountState? s, string unit) =>
        s == null ? ""
        : s.NoGame ? "\n\nゲームの Data.p4k が見つからないので、取り込み済みかを調べられません。設定タブのゲームのフォルダとチャンネルを確かめてください"
        : s.Counts is not { } c ? "\n\n今のゲームデータ (Data.p4k) でまとめて取り込んだ記録はありません"
        : BatchAllDone(s) ? $"\n\n今のゲームデータ (Data.p4k) では全部取り込み済みです (取り込んだもの {c.Done} {unit})。パッチでゲームデータが変わると、また使えます"
        : $"\n\n今のゲームデータ (Data.p4k) で、前にまとめて取り込んだときの対象のうち、取り込んだもの {c.Done} {unit}・取り込んでいないもの {c.NotDone} {unit}";

    // 置き場所のドライブの容量 (全体と残り) を出す (欄のフォルダで)
    private void UpdateShip3DDriveText()
    {
        if (txtShip3DDrive == null) return;
        var folder = Ship3DFolderFromBox();
        var d = Ship3DExports.DriveOf(folder);
        txtShip3DDrive.Text = d is { } x
            ? $"ドライブ {x.Root}  全体 {x.Total / Ship3DMB:N0} MB / 残り {x.Free / Ship3DMB:N0} MB (最大容量は残りより小さい値だけ指定できます)"
            : "置き場所のドライブの容量を調べられません";
    }

    private void UpdateShip3DUsageText()
    {
        if (txtShip3DUsage == null) return;
        // 船とステーションの地図を合わせた使用量 (最大容量は両方を合わせて数える)
        var files = Ship3DExports.CachedFiles();
        var stations = StationMapExports.CachedFiles();
        txtShip3DUsage.Text = $"今の使用量: {Ship3DExports.FormatBytes(files.Sum(f => f.Length) + stations.Sum(f => f.Length))} ({files.Count} 隻・{stations.Count} か所)";
    }

    // ［マップをまとめて取り込む］は、船の［船舶・車両をまとめて取り込む］と同じく、CPU 使用率が 90% 以上のときと、ステーションのまとめて取り込むが動いている間と、
    // 今の Data.p4k で全部取り込み済みのとき (BatchRecordCounts) は使えない
    // (取り消しは進み具合の欄の［まとめて取り消し］。船とステーションのまとめてを両方取り消す)
    private void UpdateStationBatchButton()
    {
        if (btnStationBatch == null) return;
        var st = StationMapExports.Instance;
        var ex = Ship3DExports.Instance;
        // 数えた結果は RecountBatchTargetsAsync が出す (ここでは数えない)。動いている間はボタンは使えず、ToolTip は前のまま
        var allDone = false;
        if (!st.BatchActive)
        {
            allDone = BatchAllDone(_stationBatchCounts);
            btnStationBatch.ToolTip = _stationBatchTip + BatchRecordTip(_stationBatchCounts, "か所");
        }
        btnStationBatch.IsEnabled = !ex.CpuHigh && !st.BatchActive && !allDone;
        txtStationBatchState.Text = st.BatchActive ? st.BatchText : allDone ? "マップは今のパッチでは全部取り込み済み" : "";
    }

    private async void StationBatch_Click(object sender, RoutedEventArgs e)
    {
        var st = StationMapExports.Instance;
        btnStationBatch.IsEnabled = false;
        // 船の［まとめて取り込む］と同じく、欄の置き場所・最大容量が今の設定と違えば先に反映して保存する
        var folderNow = string.IsNullOrEmpty(App.Config.Ship3DFolder) ? Ship3DExports.DefaultFolder : App.Config.Ship3DFolder;
        if (txtShip3DMaxMB.Text.Trim() != Ship3DExports.MaxMb.ToString()
            || !string.Equals(txtShip3DFolder.Text.Trim(), folderNow, StringComparison.OrdinalIgnoreCase))
        {
            SaveShip3DSettings();
            SaveConfigToFile(showError: false);
        }
        try { await st.RunBatchAsync(this); }
        catch (Exception ex)
        {
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの地図] まとめて取り込むを始められませんでした: {ex}");
            MessageBox.Show(this, $"マップをまとめて取り込むを始められませんでした: {ex.Message}", "ステーションの地図", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            UpdateStationBatchButton();
            UpdateShip3DUsageText();
        }
    }

    // ［船舶・車両をまとめて取り込む］は、CPU 使用率が 90% 以上のときと、まとめて取り込むが動いている間と、
    // 今の Data.p4k で全部取り込み済みのとき (BatchRecordCounts) は使えない
    private void UpdateShip3DBatchButton()
    {
        if (btnShip3DBatch == null) return;
        var ex = Ship3DExports.Instance;
        // 数えた結果は RecountBatchTargetsAsync が出す (ここでは数えない)。動いている間はボタンは使えず、ToolTip は前のまま
        var allDone = false;
        if (!ex.ShipBatchActive)
        {
            allDone = BatchAllDone(_shipBatchCounts);
            btnShip3DBatch.ToolTip = _ship3DBatchTip + BatchRecordTip(_shipBatchCounts, "隻");
        }
        btnShip3DBatch.IsEnabled = !ex.CpuHigh && !ex.ShipBatchActive && !allDone;
        txtShip3DBatchState.Text = ex.CpuHigh ? $"CPU 使用率 {ex.CpuPercent:F0}% のため書き出しを始められません (90% 未満になったら使えます)"
            : allDone ? "船舶・車両は今のパッチでは全部取り込み済み" : "";
    }

    // 欄のフォルダ (空なら既定)
    private string Ship3DFolderFromBox()
    {
        var t = txtShip3DFolder.Text.Trim();
        return t.Length == 0 ? Ship3DExports.DefaultFolder : t;
    }

    private void TxtShip3DFolder_TextChanged(object sender, TextChangedEventArgs e) => UpdateShip3DDriveText();

    private void Ship3DBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "3D モデルの置き場所を選択" };
        var current = Ship3DFolderFromBox();
        if (Directory.Exists(current)) dlg.InitialDirectory = current;
        if (dlg.ShowDialog(this) == true) txtShip3DFolder.Text = dlg.FolderName;
    }

    // 「設定を保存」から: 置き場所 (既定と同じなら空にして既定に従う) と最大容量。
    // 置き場所は空 (既定 = TEMP の中) か、ドライブから始まる完全なパスだけ保存する (相対パスは保存せずに知らせ、前の値のまま)。
    // 最大容量は 1 以上で、置き場所のドライブの残り容量より小さい値だけ保存する (そうでなければ保存せずに知らせ、前の値のまま)
    private void SaveShip3DSettings()
    {
        var folder = txtShip3DFolder.Text.Trim();
        if (folder.Length > 0 && !Path.IsPathFullyQualified(folder))
        {
            MessageBox.Show(this, $"3D モデルの置き場所「{folder}」は完全なパス (例 D:\\SC3D) ではないので保存しません。前の置き場所 ({Ship3DExports.Folder}) のままです。",
                "3D モデル", MessageBoxButton.OK, MessageBoxImage.Warning);
            txtShip3DFolder.Text = Ship3DExports.Folder;
        }
        else App.Config.Ship3DFolder = string.Equals(folder, Ship3DExports.DefaultFolder, StringComparison.OrdinalIgnoreCase) ? "" : folder;

        var text = txtShip3DMaxMB.Text.Trim();
        var d = Ship3DExports.DriveOf(Ship3DFolderFromBox());
        if (!long.TryParse(text, out var maxMb) || maxMb <= 0)
        {
            MessageBox.Show(this, $"3D モデルの最大容量「{text}」は数 (MB。1 以上) ではないので保存しません。前の値 ({Ship3DExports.MaxMb:N0} MB) のままです。",
                "3D モデル", MessageBoxButton.OK, MessageBoxImage.Warning);
            txtShip3DMaxMB.Text = Ship3DExports.MaxMb.ToString();
        }
        else if (d is { } x && maxMb >= x.Free / Ship3DMB)
        {
            MessageBox.Show(this, $"3D モデルの最大容量 {maxMb:N0} MB は、置き場所のドライブ {x.Root} の残り容量 ({x.Free / Ship3DMB:N0} MB) 以上なので保存しません。" +
                $"前の値 ({Ship3DExports.MaxMb:N0} MB) のままです。", "3D モデル", MessageBoxButton.OK, MessageBoxImage.Warning);
            txtShip3DMaxMB.Text = Ship3DExports.MaxMb.ToString();
        }
        else App.Config.Ship3DMaxMB = maxMb;
        UpdateShip3DDriveText();
        UpdateShip3DUsageText();
        // 置き場所が変わるとキャッシュの有無が変わるので、［詳細］・別ウィンドウのボタンを決め直す
        Ship3DExports.Instance.NotifySettingsChanged();
    }

    // ［まとめて取り込む］: 保有している船 (所持船 my_ships と Hangar 同期の船を ResolveShipRecordNameReadOnly で record にしたもの) を先に、
    // その後に［詳細］の船の一覧 (GetDetailShipList) の残り
    private async void Ship3DBatch_Click(object sender, RoutedEventArgs e)
    {
        // 欄の置き場所・最大容量が今の設定と違えば、先に反映して保存する (［設定を保存］を押さずに［まとめて取り込む］を押すと、
        // 前の最大容量のまま見積もっていた。利用者の指摘)。値が正しくなければ SaveShip3DSettings が知らせて前の値に戻す
        var folderNow = string.IsNullOrEmpty(App.Config.Ship3DFolder) ? Ship3DExports.DefaultFolder : App.Config.Ship3DFolder;
        if (txtShip3DMaxMB.Text.Trim() != Ship3DExports.MaxMb.ToString()
            || !string.Equals(txtShip3DFolder.Text.Trim(), folderNow, StringComparison.OrdinalIgnoreCase))
        {
            SaveShip3DSettings();
            SaveConfigToFile(showError: false);
        }
        btnShip3DBatch.IsEnabled = false;
        txtShip3DBatchState.Text = "保有している船を調べています...";
        try
        {
            var workDir = WorkDir;
            var myShips = _tradeService.MyShips.Select(m => (m.Name, (string?)m.Manufacturer)).ToList();
            var (owned, rest) = await Task.Run(() =>
            {
                var all = new List<DetailShipRow>();
                var db = Path.Combine(workDir, "gamedata_cache.db");
                if (File.Exists(db))
                {
                    using var q = GameDataQueryService.OpenReadOnly(db);
                    all = q.GetDetailShipList();
                }
                var nameOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in all) nameOf.TryAdd(r.Record, r.Name);

                var h = new HangarService();
                h.SetCacheDir(workDir);
                void OnProgress(string m) => App.AppendStartupLog(workDir, $"[Hangar] {m}");
                h.OnProgress += OnProgress;
                var ownedList = new List<(string Record, string Name)>();
                try
                {
                    var names = new List<(string Name, string? Manufacturer)>(myShips);
                    foreach (var s in h.GetShipInstancesReadOnly()) names.Add((s.Name, s.Matrix?.ManufacturerName));
                    foreach (var (name, mfr) in names)
                    {
                        var record = h.ResolveShipRecordNameReadOnly(name, mfr);
                        if (record == null) continue;
                        ownedList.Add((record, nameOf.TryGetValue(record, out var n) ? n : name));
                    }
                }
                finally { h.OnProgress -= OnProgress; }
                return (ownedList, all.Select(r => (r.Record, r.Name)).ToList());
            });
            txtShip3DBatchState.Text = "";
            if (owned.Count == 0 && rest.Count == 0)
            {
                MessageBox.Show(this, "船の一覧がありません (設定タブの［インデックス構築］が要ります)。", "3D モデル", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            await Ship3DExports.Instance.RunBatchAsync(owned, rest, this);
        }
        catch (Exception ex)
        {
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[3D モデル] まとめて取り込むを始められませんでした: {ex}");
            MessageBox.Show(this, $"船舶・車両をまとめて取り込むを始められませんでした: {ex.Message}", "3D モデル", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            UpdateShip3DBatchButton();
            UpdateShip3DUsageText();
        }
    }
}
