using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace StarCitizenJapaneseTextCreater;

// 自動更新の画面の文字 (利用者の確認待ちの所があるので、ここにまとめて差し替えやすくしてある)
internal static class UpdateTexts
{
    public const string WindowTitle = "アップデートがあります";
    public const string Question = "新しい版 v{0} があります。ダウンロードしますか？";          // {0} = 新しい版
    public const string CurrentVersion = "今の版: v{0}";
    public const string DownloadSize = "ダウンロードの大きさ: {0}（変わったファイル {1} 個）";  // {0} = 大きさ, {1} = ファイルの数
    public const string NotesHeader = "更新内容";
    public const string NotesFailed = "更新内容を読めませんでした（リリースのページ: {0}）";
    public const string NoNotesForVersion = "(更新内容の記載なし)";
    public const string BtnUpdate = "終了して更新";
    public const string BtnNextCheck = "次回更新時に知らせる";
    public const string BtnSkip = "今は更新しない";
    public const string BtnManual = "後で手動で更新する";
    public const string BtnCancel = "中止";
    public const string BtnOpenRelease = "リリースのページを開く";
    public const string BtnClose = "閉じる";
    public const string Downloading = "ダウンロード中… {0} / {1}";
    public const string DownloadFailed = "ダウンロードに失敗しました: {0}\nアプリはこのまま使えます。";
    public const string DownloadDone = "ダウンロードが終わりました。アプリを閉じて入れ替え、起動し直します…";
    public const string ManualOff = "自動更新を切りました。設定タブの［更新を確認］で更新できます（自動更新は設定タブで入れ直せます）";
    public const string ManualNeededTitle = "新しい版があります";
    public const string Skipped = "v{0} は見送ります。もっと新しい版が出たら知らせます";
    public const string NextCheck = "次に更新を確かめるときに、また知らせます";
    public const string DbNotReplaced = "作業フォルダがインストール先と同じなので、データベースは入れ替えません";
    public const string UpdatingTitle = "更新を入れています";
    public const string UpdatingMessage = "更新を入れています…\n終わったら起動します。";
    public const string UpdatingTimeout = "更新を入れるのに時間がかかっています。少し待ってから、もう一度起動してください。";

    public static string Size(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:N1} MB" : $"{Math.Max(1, bytes / 1024.0):N0} KB";
}

// 更新を知らせる窓。最前面 (Topmost) に出すが、フォーカスは奪わない (ShowActivated = false で Show。モーダルにしない)。
// ゲームを遊んでいる最中でも気づけるように、作業領域の右下に出す (全画面の排他モードのゲームの上には出ない)。
// 選んだ結果は Choice で返す (窓を × で閉じたら NextCheck)。［終了して更新］は、この窓の中でダウンロードの進み具合を出し、
// 取り終えたら Downloaded を起こす (呼び出し側がアプリを閉じる)。失敗したら窓に出し、選び直せるようにする
internal sealed class UpdateWindow : Window
{
    public enum UpdateChoice { None, Update, NextCheck, Skip, Manual, Cancel }

    public UpdateChoice Choice { get; private set; } = UpdateChoice.None;
    public event Action<UpdateChoice>? Chosen;

    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly ProgressBar _progress = new() { Height = 14, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed, Maximum = 1000 };
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Func<IProgress<(long Done, long Total)>, CancellationToken, Task>? _download;
    private CancellationTokenSource? _downloadCts;
    private bool _downloading;

    // download: ［終了して更新］で呼ぶダウンロード (null なら［終了して更新］を出さない)。manualOnly: 手の［更新を確認］から (［終了して更新］［中止］だけ)
    public UpdateWindow(AppUpdater.CheckResult r, Version current, Func<IProgress<(long Done, long Total)>, CancellationToken, Task>? download, bool manualOnly)
    {
        _download = download;
        Title = r.Kind == AppUpdater.CheckKind.ManualNeeded ? UpdateTexts.ManualNeededTitle : UpdateTexts.WindowTitle;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = true;
        Width = 620;
        Height = 560;
        MinWidth = 420;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width - 16;
        Top = wa.Bottom - Height - 16;

        var root = new DockPanel { Margin = new Thickness(14) };
        var head = new StackPanel();
        DockPanel.SetDock(head, Dock.Top);
        head.Children.Add(new TextBlock
        {
            Text = r.Kind == AppUpdater.CheckKind.ManualNeeded ? r.Message : string.Format(UpdateTexts.Question, r.Version),
            FontSize = 16, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap,
        });
        head.Children.Add(new TextBlock { Text = string.Format(UpdateTexts.CurrentVersion, current.ToString(3)), Foreground = Brushes.DimGray, Margin = new Thickness(0, 4, 0, 0) });
        if (r.Kind == AppUpdater.CheckKind.Available)
            head.Children.Add(new TextBlock { Text = string.Format(UpdateTexts.DownloadSize, UpdateTexts.Size(r.DownloadBytes), r.ChangedFiles), Margin = new Thickness(0, 2, 0, 0) });
        if (!string.IsNullOrEmpty(r.DbNote))
            head.Children.Add(new TextBlock { Text = r.DbNote, Foreground = Brushes.DarkOrange, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        head.Children.Add(new TextBlock { Text = UpdateTexts.NotesHeader, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 4) });
        root.Children.Add(head);

        var foot = new StackPanel();
        DockPanel.SetDock(foot, Dock.Bottom);
        foot.Children.Add(_progress);
        foot.Children.Add(_status);
        foot.Children.Add(_buttons);
        root.Children.Add(foot);

        // 更新内容 (Markdown を読める形にして、スクロールできる欄に全部)
        FlowDocument doc;
        if (r.NotesFailed || string.IsNullOrWhiteSpace(r.ReleaseNotes))
        {
            doc = new FlowDocument { PagePadding = new Thickness(0), FontSize = 13 };
            var p = new Paragraph();
            var text = string.Format(UpdateTexts.NotesFailed, r.ReleaseUrl);
            var at = text.IndexOf(r.ReleaseUrl, StringComparison.Ordinal);
            if (!string.IsNullOrEmpty(r.ReleaseUrl) && at >= 0)
            {
                p.Inlines.Add(new Run(text[..at]));
                var link = new Hyperlink(new Run(r.ReleaseUrl)) { NavigateUri = new Uri(r.ReleaseUrl) };
                link.RequestNavigate += (_, e) => OpenUrl(e.Uri.ToString());
                p.Inlines.Add(link);
                p.Inlines.Add(new Run(text[(at + r.ReleaseUrl.Length)..]));
            }
            else p.Inlines.Add(new Run(text));
            doc.Blocks.Add(p);
        }
        else doc = ChatTemplateSelector.MarkdownToFlowDocument(r.ReleaseNotes, false);
        root.Children.Add(new Border
        {
            BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), Padding = new Thickness(6),
            Child = new FlowDocumentScrollViewer { Document = doc, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsToolBarVisible = false },
        });
        Content = root;

        if (r.Kind == AppUpdater.CheckKind.ManualNeeded)
        {
            if (!string.IsNullOrEmpty(r.ReleaseUrl)) AddButton(UpdateTexts.BtnOpenRelease, () => { OpenUrl(r.ReleaseUrl); Finish(UpdateChoice.Skip); });
            AddButton(UpdateTexts.BtnClose, () => Finish(UpdateChoice.Skip));
        }
        else if (manualOnly)
        {
            AddButton(UpdateTexts.BtnUpdate, StartDownload, primary: true);
            AddButton(UpdateTexts.BtnCancel, () => Finish(UpdateChoice.Cancel));
        }
        else
        {
            AddButton(UpdateTexts.BtnUpdate, StartDownload, primary: true);
            AddButton(UpdateTexts.BtnNextCheck, () => Finish(UpdateChoice.NextCheck));
            AddButton(UpdateTexts.BtnSkip, () => Finish(UpdateChoice.Skip));
            AddButton(UpdateTexts.BtnManual, () => Finish(UpdateChoice.Manual));
        }

        Closing += (_, e) =>
        {
            // ダウンロード中に × を押したら、ダウンロードを止めて「次回更新時に知らせる」と同じにする
            if (_downloading) _downloadCts?.Cancel();
            if (Choice == UpdateChoice.None)
            {
                Choice = manualOnly ? UpdateChoice.Cancel : UpdateChoice.NextCheck;
                Chosen?.Invoke(Choice);
            }
        };
    }

    private void AddButton(string text, Action onClick, bool primary = false)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(6, 0, 0, 0), MinWidth = 90 };
        if (primary) { b.FontWeight = FontWeights.Bold; b.Background = new SolidColorBrush(Color.FromRgb(0x19, 0x76, 0xD2)); b.Foreground = Brushes.White; }
        b.Click += (_, _) => onClick();
        _buttons.Children.Add(b);
    }

    private void Finish(UpdateChoice c)
    {
        Choice = c;
        Chosen?.Invoke(c);
        Close();
    }

    private async void StartDownload()
    {
        if (_download == null || _downloading) return;
        _downloading = true;
        _buttons.IsEnabled = false;
        _progress.Visibility = Visibility.Visible;
        _progress.Value = 0;
        _status.Foreground = Brushes.Black;
        _downloadCts = new CancellationTokenSource();
        var progress = new Progress<(long Done, long Total)>(p =>
        {
            _progress.Value = p.Total > 0 ? Math.Min(1000, p.Done * 1000 / p.Total) : 1000;
            _status.Text = string.Format(UpdateTexts.Downloading, UpdateTexts.Size(p.Done), UpdateTexts.Size(p.Total));
        });
        try
        {
            await _download(progress, _downloadCts.Token);
            _progress.Value = 1000;
            _status.Text = UpdateTexts.DownloadDone;
            _downloading = false;
            Choice = UpdateChoice.Update;
            Chosen?.Invoke(UpdateChoice.Update);
        }
        catch (OperationCanceledException) { _downloading = false; }
        catch (Exception ex)
        {
            _downloading = false;
            _status.Foreground = Brushes.Firebrick;
            _status.Text = string.Format(UpdateTexts.DownloadFailed, ex.Message);
            _progress.Visibility = Visibility.Collapsed;
            _buttons.IsEnabled = true;   // 選び直せる (もう一度［終了して更新］も)
        }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
