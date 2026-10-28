using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace StarCitizenJapaneseTextCreater;

// 設定タブの「3D モデル」の欄 (Ship3DExports): 置き場所 (参照…)・置き場所のドライブの容量・最大容量・今の使用量・［まとめて取り込む］
public partial class MainWindow
{
    private const long Ship3DMB = 1024 * 1024;

    // Window_Loaded から
    private void InitShip3DSettings()
    {
        txtShip3DFolder.Text = Ship3DExports.Folder;
        txtShip3DMaxMB.Text = Ship3DExports.MaxMb.ToString();
        UpdateShip3DDriveText();
        UpdateShip3DUsageText();
        UpdateShip3DBatchButton();
        // CPU 使用率が 90% をまたいだ・書き出し・照合・キャッシュを書いた / 消したとき
        Ship3DExports.Instance.StateChanged += () => { UpdateShip3DUsageText(); UpdateShip3DBatchButton(); };
        Ship3DExports.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Ship3DExports.BatchActive)) { UpdateShip3DBatchButton(); UpdateShip3DUsageText(); }
        };
    }

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
        var files = Ship3DExports.CachedFiles();
        txtShip3DUsage.Text = $"今の使用量: {Ship3DExports.FormatBytes(files.Sum(f => f.Length))} ({files.Count} 隻)";
    }

    // ［まとめて取り込む］は、CPU 使用率が 90% 以上のときと、まとめて取り込むが動いている間は使えない
    private void UpdateShip3DBatchButton()
    {
        if (btnShip3DBatch == null) return;
        var ex = Ship3DExports.Instance;
        btnShip3DBatch.IsEnabled = !ex.CpuHigh && !ex.BatchActive;
        txtShip3DBatchState.Text = ex.CpuHigh ? $"CPU 使用率 {ex.CpuPercent:F0}% のため書き出しを始められません (90% 未満になったら使えます)" : "";
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
            MessageBox.Show(this, $"まとめて取り込むを始められませんでした: {ex.Message}", "3D モデル", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            UpdateShip3DBatchButton();
            UpdateShip3DUsageText();
        }
    }
}
