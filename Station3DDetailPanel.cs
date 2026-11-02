using System.Windows;

namespace StarCitizenJapaneseTextCreater;

// ［詳細］タブのステーションの 3D 表示のバー (DetailPanel.xaml の pnlStation3D)。船の 3D のバー (DetailPanel.xaml.cs の「船の 3D 表示」) と同じ流れ:
//   ［読み込み］: 予想を出して確かめてから裏で書き出す (Station3DExports.ImportAsync)。終わったとき、まだその場所の詳細を出していれば下の欄に 3D を出す
//   ［3D 表示］: 書き出し済み (Data.p4k が変わっていれば照合してから) なら下の欄 (web3D。船の 3D と同じ WebView2) に出す。書き出しはしない
//   ［別ウィンドウ］: 書き出し済みなら Station3DWindow に出し、この欄は詳細の表に戻す
//   ［内部のマップ］: ［マップ］タブに切り替えて、その場所の階層マップを出す (［地図表示］と同じ MapPanel.ShowPlaceInMapTab)
//   ［表に戻る］: 3D 表示をやめて詳細の表に戻す
// 出している間はその場所の 3D を「表示中」にして、容量の上限・照合で消されないようにする (Station3DExports.AcquireView)
public partial class DetailPanel
{
    // バーを出している場所 (出していなければ null) と、下の欄に 3D を出している場所の短い名前
    private ShopTreeNode? _station3DPlace;
    private string? _station3DShown;
    private bool _station3DHooked;

    // バーの場所を決める (null なら隠す)。場所が変われば 3D 表示をやめる
    private void SetStation3DTarget(ShopTreeNode? place)
    {
        if (place == null || _station3DPlace == null || !string.Equals(place.RecordName, _station3DPlace.RecordName, StringComparison.Ordinal)) HideStation3D();
        _station3DPlace = place;
        pnlStation3D.Visibility = place != null ? Visibility.Visible : Visibility.Collapsed;
        if (place == null) return;
        if (!_station3DHooked)
        {
            _station3DHooked = true;
            // 書き出しが加わった / 終わった・照合・消した・CPU 使用率が 90% をまたいだら、ボタンを決め直す
            Station3DExports.Instance.StateChanged += UpdateStation3DButtons;
            Ship3DExports.Instance.StateChanged += UpdateStation3DButtons;
            Ship3DExports.Instance.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Ship3DExports.OverallText)) UpdateStation3DButtons(); };
        }
        UpdateStation3DButtons();
        // パッチで Data.p4k が変わっていれば、この場所の元データを照合する (終わったら StateChanged でボタンを決め直す)
        if (Station3DExports.Instance.StateOf(place.RecordName) == Ship3DCacheState.NeedsVerify) _ = Station3DExports.Instance.VerifyAsync(place.RecordName);
    }

    // ［読み込み］は、3D が無い (照合で消したものを含む) ときで、書き出し中でなく、CPU 使用率が 90% 未満のときだけ使える (船の［読み込み］と同じ)
    private void UpdateStation3DButtons()
    {
        if (_station3DPlace == null) return;
        var ex = Ship3DExports.Instance;
        var state = Station3DExports.Instance.StateOf(_station3DPlace.RecordName);
        var key = Station3DExports.KeyOf(Station3DExports.ShortName(_station3DPlace.RecordName));
        var job = ex.Jobs.FirstOrDefault(j => j.Short.Equals(key, StringComparison.OrdinalIgnoreCase));
        // 自動の取り直しの順番待ちの間も押せる (押すとこの場所をいちばん先にする。Station3DExports.IsRefreshWaiting・PromoteRefresh)
        btnStation3DImport.IsEnabled = state == Ship3DCacheState.None && job == null && !ex.CpuHigh
            || Station3DExports.Instance.IsRefreshWaiting(_station3DPlace.RecordName);
        txtStation3D.Text = job != null ? $"3D: 書き出し中 {job.Text}"
            : state switch
            {
                Ship3DCacheState.Fresh => "3D: 書き出し済み",
                Ship3DCacheState.NeedsVerify or Ship3DCacheState.Verifying => "3D: 照合中 (パッチで Data.p4k が変わったので元データを確かめています)",
                Ship3DCacheState.NoGame => "3D: ゲームの Data.p4k が見つかりません",
                _ => ex.CpuHigh ? $"3D: 未書き出し。CPU 使用率 {ex.CpuPercent:F0}% のため書き出しを始められません (90% 未満になったら使えます)"
                                : "3D: 未書き出し (［読み込み］でゲームデータから書き出します)",
            };
    }

    // 表示に使える 3D があるか (Data.p4k が変わっていれば照合してから)。無ければ［読み込み］を押すよう知らせて false (書き出しはしない)
    private async Task<bool> Station3DReadyOrNotifyAsync(ShopTreeNode place)
    {
        var ex = Station3DExports.Instance;
        var state = ex.StateOf(place.RecordName);
        if (state is Ship3DCacheState.NeedsVerify or Ship3DCacheState.Verifying) txtStation3D.Text = "3D: 照合中 (パッチで Data.p4k が変わったので元データを確かめています)";
        var path = await ex.GetCachedPathAsync(place.RecordName);
        UpdateStation3DButtons();
        if (path != null) return true;
        var msg = state == Ship3DCacheState.NoGame ? "ゲームの Data.p4k が見つかりません。設定タブのゲームのフォルダとチャンネルを確かめてください。"
            : ex.IsExporting(place.RecordName) ? $"「{place.Name}」の 3D は書き出し中です。終わってから出してください。"
            : state is Ship3DCacheState.NeedsVerify or Ship3DCacheState.Verifying ? $"「{place.Name}」の元データがパッチで変わったので、3D を消しました。［読み込み］で書き出し直してください。"
            : $"「{place.Name}」の 3D はまだ書き出していません。［読み込み］でゲームデータから書き出してください。";
        MessageBox.Show(Window.GetWindow(this), msg, "ステーションの 3D 表示", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private async void BtnStation3DImport_Click(object sender, RoutedEventArgs e)
    {
        var place = _station3DPlace;
        if (place == null) return;
        btnStation3DImport.IsEnabled = false;
        var path = await Station3DExports.Instance.ImportAsync(place.RecordName, place.Name, Window.GetWindow(this));
        UpdateStation3DButtons();
        if (path == null) return;
        if (!ReferenceEquals(place, _station3DPlace))
        {
            txtStatus.Text = $"「{place.Name}」の 3D を書き出しました (その場所の詳細の［3D 表示］で出せます)";
            return;
        }
        await ShowStation3DAsync(place);
    }

    private async void BtnStation3D_Click(object sender, RoutedEventArgs e)
    {
        var place = _station3DPlace;
        if (place == null) return;
        if (!await Station3DReadyOrNotifyAsync(place)) return;
        if (!ReferenceEquals(place, _station3DPlace)) return;
        await ShowStation3DAsync(place);
    }

    private async void BtnStation3DWindow_Click(object sender, RoutedEventArgs e)
    {
        var place = _station3DPlace;
        if (place == null) return;
        var owner = Window.GetWindow(this);
        if (!await Station3DReadyOrNotifyAsync(place)) return;
        try
        {
            var win = new Station3DWindow(place.RecordName, place.Name);
            if (owner != null && owner.IsLoaded) win.Owner = owner;
            win.Show();
            // 別ウィンドウで出したら、この欄は詳細の表に戻す (船の［別ウィンドウ］と同じ。同じ 3D を 2 か所で出さない)
            HideStation3D();
        }
        catch (Exception ex)
        {
            AppendAppLog($"[ステーションの 3D] 別ウィンドウを開けませんでした: {ex}");
            MessageBox.Show($"別ウィンドウを開けませんでした: {ex.Message}", "ステーションの 3D 表示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ［内部のマップ］: ［地図表示］と同じ (取り込み済みかどうかは見ない。未取り込みなら［マップ］タブが知らせと［読み込み］を出す)
    private void BtnStation3DMap_Click(object sender, RoutedEventArgs e)
    {
        if (_station3DPlace is not { } place) return;
        MapPanel.ShowPlaceInMapTab(place.RecordName, place.Name);
    }

    private void BtnStation3DBack_Click(object sender, RoutedEventArgs e) => HideStation3D();

    // 下の欄に 3D を出す (詳細の表の WebView2 は隠すだけで、中身はそのまま。船の ShowShip3DAsync と同じ)
    private async Task ShowStation3DAsync(ShopTreeNode place)
    {
        try { await (_web3DInit ??= Ship3DWeb.InitAsync(web3D)); }
        catch (Exception ex)
        {
            _web3DInit = null;
            txtStation3D.Text = $"3D 表示 (WebView2) を使えません: {ex.Message}";
            AppendAppLog($"[ステーションの 3D] WebView2 の初期化に失敗しました: {ex.GetType().FullName} (HResult 0x{ex.HResult:X8}): {ex}");
            return;
        }
        if (!ReferenceEquals(place, _station3DPlace)) return;
        var shortName = Station3DExports.ShortName(place.RecordName);
        if (_station3DShown != null) Station3DExports.Instance.ReleaseView(_station3DShown);
        Station3DExports.Instance.AcquireView(shortName);
        _station3DShown = shortName;
        bool shown;
        try { shown = await Station3DWeb.ShowAsync(web3D, shortName, place.Name, () => _station3DShown == shortName); }
        catch (Exception ex)
        {
            AppendAppLog($"[ステーションの 3D] 表示できませんでした: {ex}");
            txtStation3D.Text = $"3D 表示できませんでした: {ex.Message}";
            HideStation3D();
            return;
        }
        // 見出しの言葉を引く間に別の場所に移っていたら出さない (移ったときの HideStation3D で表示中も外れている)
        if (!shown) return;
        webDetail.Visibility = Visibility.Collapsed;
        txtDetail.Visibility = Visibility.Collapsed;
        web3D.Visibility = Visibility.Visible;
        btnStation3DBack.Visibility = Visibility.Visible;
    }

    // 3D 表示をやめて詳細の表に戻す (3D のページは空にして、読み込んだ GLB の分のメモリを放す)
    private void HideStation3D()
    {
        if (_station3DShown == null) return;
        Station3DExports.Instance.ReleaseView(_station3DShown);
        _station3DShown = null;
        Station3DWeb.Blank(web3D);
        web3D.Visibility = Visibility.Collapsed;
        btnStation3DBack.Visibility = Visibility.Collapsed;
        if (_webFailed) txtDetail.Visibility = Visibility.Visible;
        else webDetail.Visibility = Visibility.Visible;
    }
}
