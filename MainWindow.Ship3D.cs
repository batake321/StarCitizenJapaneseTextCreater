using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace StarCitizenJapaneseTextCreater;

// 設定タブの「3D モデル」の欄 (Ship3DExports): 置き場所 (参照…)・置き場所のドライブの容量・最大容量・今の使用量 (船)・保存しない・［船舶・車両をまとめて取り込む］・
// ［マップをまとめて取り込む］(StationView3DLayout。区画の置き方の一覧を作り直す)
public partial class MainWindow
{
    private const long Ship3DMB = 1024 * 1024;

    // 船のまとめて取り込むのボタンの ToolTip の元の文 (XAML のもの。後ろに今の Data.p4k で取り込んだ数・取り込んでいない数を足して出す)
    private string _ship3DBatchTip = "";

    // Window_Loaded から
    private void InitShip3DSettings()
    {
        _ship3DBatchTip = btnShip3DBatch.ToolTip as string ?? "";
        txtShip3DFolder.Text = Ship3DExports.Folder;
        txtShip3DMaxMB.Text = Ship3DExports.MaxMb.ToString();
        chkShip3DNoSave.IsChecked = App.Config.Ship3DNoSave;
        chkShip3DPalette.IsChecked = App.Config.Ship3DPalette;
        txtStation3DCacheMaxGB.Text = Station3DCacheMaxGbText();
        txtStation3DTextureMaxPx.Text = App.Config.Station3DTextureMaxPx.ToString(System.Globalization.CultureInfo.InvariantCulture);
        chkStation3DDebugView.IsChecked = App.Config.Station3DDebugView;
        UpdateStation3DCacheUsageText();
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
                // 船のまとめてが始まった・終わったときだけ数え直す
                var active = Ship3DExports.Instance.ShipBatchActive;
                if (active != _lastShipBatchActive) { _lastShipBatchActive = active; RequestBatchRecount(); }
            }
        };
        // 区画の置き方の一覧 (view3d_layout.db) を作っている・作り終えたとき
        StationView3DLayout.Changed += () => Dispatcher.BeginInvoke(UpdateStationBatchButton);
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
                var ships = await Task.Run(() =>
                {
                    var stamp = Ship3DExports.CurrentStamp();
                    return CountOnDisk(BatchTargetRecord.Ships, stamp, Ship3DExports.IsFreshOnDisk);
                });
                _shipBatchCounts = FinishCount(ships, Ship3DExports.Instance.HeldBack);
                UpdateShip3DBatchButton();
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
        // 船の使用量 (2026-11-04、ステーションの 2D の地図・古い方式のステーションの 3D を消したので船だけ)
        var files = Ship3DExports.CachedFiles();
        txtShip3DUsage.Text = $"今の使用量: {Ship3DExports.FormatBytes(files.Sum(f => f.Length))} ({files.Count} 件)";
    }

    // ［マップをまとめて取り込む］は、船の［船舶・車両をまとめて取り込む］と同じく、CPU 使用率が 90% 以上のときと、ステーションのまとめて取り込むが動いている間と、
    // 今の Data.p4k で全部取り込み済みのとき (BatchRecordCounts) は使えない
    // (取り消しは進み具合の欄の［まとめて取り消し］。船とステーションのまとめてを両方取り消す)
    // (2026-11-04、利用者の指示「もう、2D は使わない手、マップの取り込みは取り込む場所関係じゃないの？」) ［マップをまとめて取り込む］は、
    // 3D (新しい方式) の区画の置き方の一覧と場所の中身 (view3d_layout.db。StationView3DLayout) を、全部の場所の分まとめて作り直す。2D の地図は作らない。
    // 動いている間はボタンを使えなくし、横に済んだ数を出す (0.5 秒ごと)
    private System.Windows.Threading.DispatcherTimer? _layoutTimer;
    private void UpdateStationBatchButton()
    {
        if (btnStationBatch == null) return;
        var running = StationView3DLayout.Running;
        btnStationBatch.IsEnabled = !running;
        if (running)
        {
            var (d, n) = StationView3DLayout.Progress;
            txtStationBatchState.Text = $"区画の置き方の一覧を作っています ({d} / {n} か所)";
            if (_layoutTimer == null)
            {
                _layoutTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _layoutTimer.Tick += (_, _) => UpdateStationBatchButton();
                _layoutTimer.Start();
            }
        }
        else
        {
            _layoutTimer?.Stop(); _layoutTimer = null;
            // 問題が無ければ無効 (船の［船舶・車両をまとめて取り込む］の「全部取り込み済みなら無効」と同じ形)、問題があれば有効にして横に理由
            var problem = StationView3DLayout.Problem();
            btnStationBatch.IsEnabled = problem != null;
            txtStationBatchState.Text = problem == null ? "区画の置き方の一覧は今のパッチでは作り済み"
                : StationView3DLayout.LastError is { } err ? $"区画の置き方の一覧を作れませんでした: {err} ({problem})"
                : $"区画の置き方の一覧: {problem}";
        }
    }

    private void StationBatch_Click(object sender, RoutedEventArgs e)
    {
        btnStationBatch.IsEnabled = false;
        StationView3DLayout.Start("［マップをまとめて取り込む］", force: true);
        UpdateStationBatchButton();
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
        // (2026-11-04) 「3D モデルを保存しない」のときは、取り込み (保存) のためのまとめて取り込むは使えない
        btnShip3DBatch.IsEnabled = !ex.CpuHigh && !ex.ShipBatchActive && !allDone && !Ship3DExports.NoSave;
        txtShip3DBatchState.Text = Ship3DExports.NoSave && !ex.ShipBatchActive ? "「3D モデルを保存しない」のため使えません"
            : ex.CpuHigh ? $"CPU 使用率 {ex.CpuPercent:F0}% のため書き出しを始められません (90% 未満になったら使えます)"
            : allDone ? "船舶・車両は今のパッチでは全部取り込み済み" : "";
    }

    // ===== ステーションの 3D の取り出し結果の保存 (StationView3DCache) の上限 (GB) =====
    // (2026-11-04、利用者の指示「先読み、キャッシュ化 HDD 容量設定が空いていればファイル保存など早く動かす仕組みを入れて」)
    private static string Station3DCacheMaxGbText() => (StationView3DCache.MaxBytesSetting / (double)(1L << 30)).ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);

    private void UpdateStation3DCacheUsageText()
    {
        if (txtStation3DCacheUsage == null) return;
        var cache = StationView3DCache.Current;
        _ = Task.Run(() => cache.UsedBytes()).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully) txtStation3DCacheUsage.Text = $"今の使用量: {Ship3DExports.FormatBytes(t.Result)}";
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // 「設定を保存」から: 0 以上の数 (GB) で、置き場所のドライブの残り容量より小さい値だけ保存する (そうでなければ保存せずに知らせ、前の値のまま)。
    // 上限が下がったら、すぐに古いものから消す (0 なら全部。使われている最中のものは残る)
    private void SaveStation3DCacheSettings()
    {
        var text = txtStation3DCacheMaxGB.Text.Trim();
        var d = Ship3DExports.DriveOf(Ship3DFolderFromBox());
        var ok = double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var gb)
                 || double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out gb);
        if (!ok || gb < 0 || double.IsNaN(gb) || double.IsInfinity(gb))
        {
            MessageBox.Show(this, $"ステーションの 3D の保存の上限「{text}」は数 (GB。0 以上。0 なら保存しない) ではないので保存しません。前の値 ({Station3DCacheMaxGbText()} GB) のままです。",
                "3D モデル", MessageBoxButton.OK, MessageBoxImage.Warning);
            txtStation3DCacheMaxGB.Text = Station3DCacheMaxGbText();
        }
        else if (d is { } x && gb * (1L << 30) >= x.Free)
        {
            MessageBox.Show(this, $"ステーションの 3D の保存の上限 {gb:0.##} GB は、置き場所のドライブ {x.Root} の残り容量 ({x.Free / (double)(1L << 30):0.##} GB) 以上なので保存しません。" +
                $"前の値 ({Station3DCacheMaxGbText()} GB) のままです。", "3D モデル", MessageBoxButton.OK, MessageBoxImage.Warning);
            txtStation3DCacheMaxGB.Text = Station3DCacheMaxGbText();
        }
        else App.Config.Station3DCacheMaxGB = gb;
        // ステーションの 3D に貼るゲームの画像の長い辺の上限 (px。0 以上の整数。0 なら貼らない)。そうでなければ保存せずに知らせ、前の値のまま (2026-11-05)
        var ptext = txtStation3DTextureMaxPx.Text.Trim();
        if (!int.TryParse(ptext, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var px) || px < 0)
        {
            MessageBox.Show(this, $"ステーションのテクスチャの上限「{ptext}」は数 (px。0 以上の整数。0 なら画像を貼らない) ではないので保存しません。前の値 ({App.Config.Station3DTextureMaxPx}) のままです。",
                "3D モデル", MessageBoxButton.OK, MessageBoxImage.Warning);
            txtStation3DTextureMaxPx.Text = App.Config.Station3DTextureMaxPx.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        else App.Config.Station3DTextureMaxPx = px;
        // マップのデバッグ表示 (ステーションの 3D の下部の枠。既定は出さない。2026-11-05)。次にページを開いたときから効く (StationView3DWindow が URL の debug=1 で渡す)
        App.Config.Station3DDebugView = chkStation3DDebugView.IsChecked == true;
        StationView3DCache.NotifySettingsChanged();
        var cache = StationView3DCache.Current;
        _ = Task.Run(() => { cache.Evict(); }).ContinueWith(_ => UpdateStation3DCacheUsageText(), TaskScheduler.FromCurrentSynchronizationContext());
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
        App.Config.Ship3DNoSave = chkShip3DNoSave.IsChecked == true;
        // 船・装備・武器の 3D の色付け (既定は付ける。2026-11-05)。次に 3D を開いたときから効く (Ship3DWeb がビューアに渡す)
        App.Config.Ship3DPalette = chkShip3DPalette.IsChecked == true;
        SaveStation3DCacheSettings();
        UpdateShip3DDriveText();
        UpdateShip3DBatchButton();
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
