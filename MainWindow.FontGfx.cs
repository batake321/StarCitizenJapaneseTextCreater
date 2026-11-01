using System.IO;
using System.Windows;

namespace StarCitizenJapaneseTextCreater;

// 設定タブの「日本語フォント (gfx)」の欄 (FontGfxInstaller): ［フォントを置く］［元に戻す］と、置き先のファイルの状態。
//   ゲームのフォルダは RunPipeline と同じく上のバーの欄 (txtGamePath) の値を使う (ボタンを押したときに App.Config.GamePath に入れる)。
//   状態の欄は、起動したときと設定タブを開いたときに、置き先のファイルの有無と大きさを出す (ネットにはつながない)
public partial class MainWindow
{
    private const string FontGfxTitle = "日本語フォント";
    private bool _fontGfxBusy;

    // Window_Loaded から
    private void InitFontGfx()
    {
        UpdateFontGfxStatus();
        // 上のバーのチャンネルを切り替えると置き先が変わるので、設定タブを開いたときにも出し直す
        tabMain.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, tabMain) && ReferenceEquals(tabMain.SelectedItem, tabSettings) && !_fontGfxBusy)
                UpdateFontGfxStatus();
        };
    }

    // 状態の欄: result (直前の結果) があれば 1 行目に出し、その下に置き先のファイルの有無と大きさ
    private void UpdateFontGfxStatus(string? result = null)
    {
        if (txtFontGfxStatus == null) return;
        string state;
        var gamePath = txtGamePath.Text.Trim();
        if (gamePath.Length == 0) state = "ゲームのフォルダが設定されていません";
        else
        {
            try
            {
                var target = FontGfxInstaller.TargetPath(gamePath);
                var fi = new FileInfo(target);
                var bak = File.Exists(FontGfxInstaller.BackupPath(target)) ? $" (控え {FontGfxInstaller.GfxAssetName}.bak があります)" : "";
                state = fi.Exists
                    ? $"置き先にフォントがあります ({fi.Length:N0} バイト){bak}: {target}"
                    : $"置き先にフォントはありません (ゲームは Data.p4k の中の元のフォントを使います){bak}: {target}";
            }
            catch (Exception ex) { state = $"置き先を調べられません: {ex.Message}"; }
        }
        txtFontGfxStatus.Text = string.IsNullOrEmpty(result) ? state : $"{result}\n{state}";
    }

    private void SetFontGfxBusy(bool busy)
    {
        _fontGfxBusy = busy;
        btnFontGfxInstall.IsEnabled = !busy;
        btnFontGfxRestore.IsEnabled = !busy;
    }

    // 止めた・終えた知らせ (ログ・状態の欄・メッセージ)
    private void FontGfxNotify(string message, MessageBoxImage image)
    {
        Log($"[フォント] {message}");
        UpdateFontGfxStatus(message);
        MessageBox.Show(this, message, FontGfxTitle, MessageBoxButton.OK, image);
    }

    // ［フォントを置く］
    private async void FontGfxInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_fontGfxBusy) return;
        // gfx は日本語用なので、書き出す言語が japanese_(japan) のときだけ置く
        if (!string.Equals((App.Config.OutputLanguage ?? "").Trim(), FontGfxInstaller.Language, StringComparison.OrdinalIgnoreCase))
        {
            FontGfxNotify($"書き出す言語 (Output Language) が「{App.Config.OutputLanguage}」なので置きません。このフォントは日本語 ({FontGfxInstaller.Language}) 用です。", MessageBoxImage.Warning);
            return;
        }
        App.Config.GamePath = txtGamePath.Text.Trim();
        var gamePath = App.Config.GamePath;
        if (gamePath.Length == 0 || !Directory.Exists(gamePath))
        {
            FontGfxNotify($"ゲームのフォルダが見つかりません ({gamePath})。上のバーのゲームのフォルダを確かめてください。", MessageBoxImage.Warning);
            return;
        }
        if (IsGameProcessRunning())
        {
            FontGfxNotify("ゲーム (StarCitizen) が起動しています。ゲームを終了してから押してください。", MessageBoxImage.Warning);
            return;
        }

        SetFontGfxBusy(true);
        var target = FontGfxInstaller.TargetPath(gamePath);
        var tmp = FontGfxInstaller.DownloadPath(target);
        string? createdDir = null;
        var placed = false;
        string? result = null;
        try
        {
            txtFontGfxStatus.Text = "公開物のフォントを探しています…";
            var (tag, infoUrl, gfxUrl) = await FontGfxInstaller.FindAssetsAsync();
            if (infoUrl == null || gfxUrl == null)
            {
                result = $"この版の公開物 ({tag}) にフォントがありません ({FontGfxInstaller.GfxAssetName} / {FontGfxInstaller.InfoAssetName})。置きませんでした。";
                FontGfxNotify(result, MessageBoxImage.Warning);
                return;
            }

            txtFontGfxStatus.Text = $"フォントを取っています ({tag})…";
            var info = await FontGfxInstaller.DownloadInfoAsync(infoUrl);
            createdDir = FontGfxInstaller.CreateFolder(target);
            await FontGfxInstaller.DownloadGfxAsync(gfxUrl, tmp, info);
            Log($"[フォント] {tag} のフォントを取りました ({info.Size:N0} バイト、SHA-256 {info.Sha256})");

            txtFontGfxStatus.Text = "ゲームの元のフォントを確かめています…";
            var (kind, reason) = await FontGfxInstaller.CheckBaseAsync(info);
            if (kind != FontGfxInstaller.BaseCheck.Same)
            {
                Log($"[フォント] {reason}");
                var head = kind == FontGfxInstaller.BaseCheck.Changed
                    ? "ゲームの元のフォントが、このフォントを作ったときと変わっています。"
                    : "ゲームの元のフォントが、このフォントを作ったときと同じか確かめられません。";
                var ask = $"{head}置くと字の形や並びが合わない恐れがあります。それでも置きますか？\n\n({reason})";
                if (MessageBox.Show(this, ask, FontGfxTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    result = "フォントを置くのをやめました。";
                    Log($"[フォント] {result}");
                    return;
                }
            }

            // 取っている間・聞いている間にゲームを起動されていないか、置く直前にもう一度確かめる (起動中なら一時ファイルは finally で消す)。
            // 確かめと置く処理 (SHA-256 の計算・ファイルの移動) は、間に何も入らないよう続けて UI スレッドの外で動かす
            var placeResult = await Task.Run(() =>
                IsGameProcessRunning() ? ((string Note, string? MarkerError)?)null : FontGfxInstaller.Place(tmp, target, info));
            if (placeResult is not { } pr)
            {
                result = "ゲーム (StarCitizen) が起動しているので置きませんでした。ゲームを終了してから押してください。";
                FontGfxNotify(result, MessageBoxImage.Warning);
                return;
            }
            placed = true;
            if (pr.Note.Length > 0) Log($"[フォント] {pr.Note}");
            result = $"フォントを置きました ({tag}、{info.Size:N0} バイト)。効くのはゲームを起動し直してからです。";
            if (pr.MarkerError != null)
            {
                // フォントは置けているので成功として扱い、印を書けなかったことは注意として出す
                var markerName = Path.GetFileName(FontGfxInstaller.MarkerPath(target));
                result += $"\n注意: 印 ({markerName}) を書けませんでした ({pr.MarkerError})。次に置くとき・元に戻すときに、このアプリが置いたものか見分けられないことがあります。";
            }
            Log($"[フォント] {result} {target}");
            MessageBox.Show(this, $"{result}\n\n{target}{(pr.Note.Length > 0 ? $"\n\n{pr.Note}" : "")}", FontGfxTitle, MessageBoxButton.OK,
                pr.MarkerError != null ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            result = $"フォントを置けませんでした: {ex.Message}";
            Log($"[フォント] {result}");
            MessageBox.Show(this, result, FontGfxTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (!placed)
            {
                FontGfxInstaller.TryDelete(tmp);
                FontGfxInstaller.RemoveCreatedFolders(target, createdDir);
            }
            SetFontGfxBusy(false);
            UpdateFontGfxStatus(result);
        }
    }

    // ［元に戻す］: このアプリが置いたフォントを消し、控え (.bak) があれば戻す。
    // 公開物の json が取れない (ネットにつながらない・公開物に無い) ときも、確かめられないことを言って聞いた上で消せる
    private async void FontGfxRestore_Click(object sender, RoutedEventArgs e)
    {
        if (_fontGfxBusy) return;
        App.Config.GamePath = txtGamePath.Text.Trim();
        var gamePath = App.Config.GamePath;
        if (gamePath.Length == 0)
        {
            FontGfxNotify("ゲームのフォルダが設定されていません。上のバーのゲームのフォルダを確かめてください。", MessageBoxImage.Warning);
            return;
        }
        if (IsGameProcessRunning())
        {
            FontGfxNotify("ゲーム (StarCitizen) が起動しています。ゲームを終了してから押してください。", MessageBoxImage.Warning);
            return;
        }
        var target = FontGfxInstaller.TargetPath(gamePath);
        var bak = FontGfxInstaller.BackupPath(target);
        if (!File.Exists(target) && !File.Exists(bak))
        {
            FontGfxNotify("置き先にフォントはありません。元に戻すものはありません。", MessageBoxImage.Information);
            return;
        }

        SetFontGfxBusy(true);
        string? result = null;
        try
        {
            if (File.Exists(target))
            {
                txtFontGfxStatus.Text = "置き先のフォントがこのアプリが置いたものか確かめています…";
                FontGfxInstaller.FontInfo? info = null;
                string tag = "", why = "";
                try
                {
                    (tag, var infoUrl, _) = await FontGfxInstaller.FindAssetsAsync();
                    if (infoUrl == null) why = $"この版の公開物 ({tag}) にフォントの記録 ({FontGfxInstaller.InfoAssetName}) がありません";
                    else info = await FontGfxInstaller.DownloadInfoAsync(infoUrl);
                }
                catch (Exception ex) { why = $"公開物のフォントの記録 ({FontGfxInstaller.InfoAssetName}) を取れませんでした: {ex.Message}"; }
                var sha = await Task.Run(() => AppUpdater.Sha256OfFile(target));
                var marker = FontGfxInstaller.ReadMarker(target);

                // json か印の値と一致すれば、このアプリが置いたもの。どちらとも一致しない・json が取れず印も無いときは、確かめられないと言って聞く
                string ask;
                if (FontGfxInstaller.IsOurs(sha, info, marker)) ask = $"このアプリが置いたフォントを消しますか？\n\n{target}";
                else
                {
                    var markerName = Path.GetFileName(FontGfxInstaller.MarkerPath(target));
                    var infoPart = info != null ? $"公開物の記録 ({tag}) と SHA-256 が一致しません" : why;
                    var markerPart = marker != null ? $"印 ({markerName}) の値とも一致しません" : $"印 ({markerName}) はありません";
                    why = $"{infoPart}。{markerPart}";
                    Log($"[フォント] {why}");
                    ask = $"置き先のフォントが、このアプリが置いたものか確かめられません ({why})。消しますか？\n\n{target}";
                }
                if (File.Exists(bak)) ask += $"\n\n消したあと、控え ({Path.GetFileName(bak)}) を置き先に戻します。";
                if (MessageBox.Show(this, ask, FontGfxTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                {
                    result = "元に戻すのをやめました。";
                    Log($"[フォント] {result}");
                    return;
                }
            }

            // 聞いている間にゲームを起動されていないか、消す直前にもう一度確かめる。
            // 確かめと消す処理は、間に何も入らないよう続けて UI スレッドの外で動かす
            var hadTarget = File.Exists(target);
            var removeResult = await Task.Run(() => IsGameProcessRunning() ? (bool?)null : FontGfxInstaller.Remove(target));
            if (removeResult is not { } restored)
            {
                result = "ゲーム (StarCitizen) が起動しているので元に戻しませんでした。ゲームを終了してから押してください。";
                FontGfxNotify(result, MessageBoxImage.Warning);
                return;
            }
            result = restored
                ? (hadTarget ? $"置いたフォントを消し、控え ({Path.GetFileName(bak)}) を置き先に戻しました。" : $"控え ({Path.GetFileName(bak)}) を置き先に戻しました。")
                : "置いたフォントを消しました。ゲームは Data.p4k の中の元のフォントを使います。";
            Log($"[フォント] {result} {target}");
            MessageBox.Show(this, $"{result}\n\n{target}", FontGfxTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            result = $"元に戻せませんでした: {ex.Message}";
            Log($"[フォント] {result}");
            MessageBox.Show(this, result, FontGfxTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetFontGfxBusy(false);
            UpdateFontGfxStatus(result);
        }
    }
}
