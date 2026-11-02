using System.ComponentModel;
using System.Windows;

namespace StarCitizenJapaneseTextCreater;

// ステーションの 3D 表示の別ウィンドウ (モードレス)。船の 3D の別ウィンドウ (Ship3DWindow) の表示の流れにならう:
//   書き出し済み (最新。Data.p4k が変わっていれば照合して同じだったもの) ならすぐ出す。無ければ自動で書き出さず［読み込み］を出し、書き出しが終わったらこの窓に出す。
//   出している間はその場所の 3D を「表示中」にして、容量の上限・照合で消されないようにする (Station3DExports.AcquireView)。
//   ［内部のマップ］はメインの画面の［マップ］タブに切り替えて、この場所の階層マップを出す (［詳細］の［地図表示］と同じ MapPanel.ShowPlaceInMapTab)
public partial class Station3DWindow : Window
{
    private readonly string _record;
    private readonly string _name;
    private string? _shownShort;
    private Task? _webInit;
    private bool _webFailed;
    private bool _showing;

    public Station3DWindow(string record, string name)
    {
        InitializeComponent();
        _record = record;
        _name = string.IsNullOrWhiteSpace(name) ? Station3DExports.ShortName(record) : name;
        Title = $"ステーションの 3D 表示 - {_name}";
        txtPlace.Text = _name;
        var st = Station3DExports.Instance;
        var ex = Ship3DExports.Instance;
        st.StateChanged += OnStateChanged;
        st.Exported += OnExported;
        ex.StateChanged += OnStateChanged;
        ex.PropertyChanged += OnExportsPropertyChanged;
        Closed += (_, _) =>
        {
            st.StateChanged -= OnStateChanged;
            st.Exported -= OnExported;
            ex.StateChanged -= OnStateChanged;
            ex.PropertyChanged -= OnExportsPropertyChanged;
            if (_shownShort != null) st.ReleaseView(_shownShort);
            _shownShort = null;
            try { web3D.Dispose(); } catch { }
        };
        Loaded += async (_, _) =>
        {
            UpdateBar();
            await ShowIfCachedAsync();
        };
    }

    // 書き出し済みなら出す (Data.p4k が変わっていれば照合してから)。無ければ［読み込み］を押すよう出す
    private async Task ShowIfCachedAsync()
    {
        if (_shownShort != null || _showing) return;
        var st = Station3DExports.Instance;
        var state = st.StateOf(_record);
        if (state == Ship3DCacheState.NoGame) { ShowOverlay("ゲームの Data.p4k が見つかりません。設定タブのゲームのフォルダとチャンネルを確かめてください。"); return; }
        if (state == Ship3DCacheState.None)
        {
            ShowOverlay(st.IsRefreshWaiting(_record)
                ? "この場所の元データがパッチで変わったので、自動の取り直しの順番を待っています。終わったらここに出します (上の［読み込み］でいちばん先にできます)。"
                : st.IsExporting(_record) ? "書き出し中です。終わったらここに出します。" : "この場所の 3D はまだ書き出していません。上の［読み込み］でゲームデータから書き出してください。");
            return;
        }
        _showing = true;
        try
        {
            if (state != Ship3DCacheState.Fresh) ShowOverlay("照合中 (パッチで Data.p4k が変わったので、この場所の元データを確かめています)...");
            var path = await st.GetCachedPathAsync(_record);
            if (_shownShort != null) return;
            if (path == null)
            {
                // 照合で変わっていた場所は自動の取り直しに並ぶ (Station3DExports.QueueRefresh) ので、まず書き出し中 (取り直しを含む) かを見る
                // (［詳細］の Station3DReadyOrNotifyAsync と同じ順)
                ShowOverlay(st.IsExporting(_record)
                    ? (st.IsRefreshWaiting(_record)
                        ? "この場所の元データがパッチで変わったので、自動の取り直しの順番を待っています。終わったらここに出します (上の［読み込み］でいちばん先にできます)。"
                        : "書き出し中です。終わったらここに出します。")
                    : "この場所の元データがパッチで変わったので、3D を消しました。上の［読み込み］で書き出し直してください。");
                return;
            }
            await ShowModelAsync();
        }
        finally
        {
            _showing = false;
            UpdateBar();
        }
    }

    private async Task ShowModelAsync()
    {
        if (_webFailed) return;
        try { await (_webInit ??= Ship3DWeb.InitAsync(web3D)); }
        catch (Exception ex)
        {
            _webFailed = true;
            ShowOverlay($"3D 表示 (WebView2) を使えません: {ex.Message}");
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D] 別ウィンドウの WebView2 の初期化に失敗しました: {ex.GetType().FullName} (HResult 0x{ex.HResult:X8}): {ex}");
            return;
        }
        if (_shownShort != null || !IsLoaded) return;
        var shortName = Station3DExports.ShortName(_record);
        Station3DExports.Instance.AcquireView(shortName);
        _shownShort = shortName;
        try
        {
            if (!await Station3DWeb.ShowAsync(web3D, shortName, _name, () => _shownShort == shortName)) return;
        }
        catch (Exception ex)
        {
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D] 別ウィンドウで表示できませんでした: {ex}");
            HideModel();
            ShowOverlay($"3D 表示できませんでした: {ex.Message}");
            return;
        }
        txtOverlay.Visibility = Visibility.Collapsed;
        web3D.Visibility = Visibility.Visible;
    }

    private void HideModel()
    {
        if (_shownShort == null) return;
        var s = _shownShort;
        _shownShort = null;
        Station3DWeb.Blank(web3D);
        web3D.Visibility = Visibility.Collapsed;
        Station3DExports.Instance.ReleaseView(s);
    }

    private void ShowOverlay(string text)
    {
        txtOverlay.Text = text;
        txtOverlay.Visibility = Visibility.Visible;
        if (_shownShort == null) web3D.Visibility = Visibility.Collapsed;
    }

    private async void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        btnImport.IsEnabled = false;
        var path = await Station3DExports.Instance.ImportAsync(_record, _name, this);
        UpdateBar();
        if (path != null) await ShowIfCachedAsync();
    }

    private void BtnMap_Click(object sender, RoutedEventArgs e) => MapPanel.ShowPlaceInMapTab(_record, _name);

    // ［読み込み］の有効・無効と状態の文 (［詳細］のバーと同じ決まり)
    private void UpdateBar()
    {
        var ex = Ship3DExports.Instance;
        var state = Station3DExports.Instance.StateOf(_record);
        var key = Station3DExports.KeyOf(Station3DExports.ShortName(_record));
        var job = ex.Jobs.FirstOrDefault(j => j.Short.Equals(key, StringComparison.OrdinalIgnoreCase));
        // 自動の取り直しの順番待ちの間も押せる (押すとこの場所をいちばん先にする。Station3DExports.IsRefreshWaiting・PromoteRefresh)
        btnImport.IsEnabled = state == Ship3DCacheState.None && job == null && !ex.CpuHigh || Station3DExports.Instance.IsRefreshWaiting(_record);
        txtState.Text = job != null ? $"書き出し中: {job.Text}"
            : state switch
            {
                Ship3DCacheState.Fresh => "書き出し済み",
                Ship3DCacheState.NeedsVerify or Ship3DCacheState.Verifying => "照合中 (パッチで Data.p4k が変わったので元データを確かめています)",
                Ship3DCacheState.NoGame => "ゲームの Data.p4k が見つかりません",
                _ => ex.CpuHigh ? $"未書き出し。CPU 使用率 {ex.CpuPercent:F0}% のため書き出しを始められません (90% 未満になったら使えます)"
                                : "未書き出し (［読み込み］でゲームデータから書き出します)",
            };
    }

    private void OnStateChanged()
    {
        UpdateBar();
        if (_shownShort == null && Station3DExports.Instance.StateOf(_record) == Ship3DCacheState.Fresh) _ = ShowIfCachedAsync();
    }

    private void OnExported(string shortName)
    {
        if (_shownShort == null && Station3DExports.ShortName(_record).Equals(shortName, StringComparison.OrdinalIgnoreCase)) _ = ShowIfCachedAsync();
    }

    private void OnExportsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Ship3DExports.OverallText)) UpdateBar();
    }
}
