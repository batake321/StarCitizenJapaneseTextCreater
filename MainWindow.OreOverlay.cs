using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace StarCitizenJapaneseTextCreater;

// 設定タブの「現在位置のキーとオーバーレイ」の欄と、現在位置のキー (MapCurrentHotkey) の登録。
//   キー: ［マップ］の［現在位置］ボタンと同じ処理を、ゲームが前にあるままキーで動かす (MapPanel.CaptureCurrentFromHotkeyAsync)。
//     使う / 使わないとキーは設定 (App.Config.MapCurrentHotkeyEnabled / MapCurrentHotkey)。キーの欄を選んでキーを押すと、押したキー (修飾キーは左右別) が入る。
//     「設定を保存」で有効になる。キーは Raw Input で受ける (MapCurrentHotkey。キーを取り上げないので、ゲームにもほかのアプリにもそのまま届く)。
//     Raw Input の登録ができなかったときは、欄の横の文とログで知らせる (例外にしない)。キーを受けたとき・修飾キーが合わずに何もしなかったとき (10 秒に 1 回まで) はログに残す。
//   オーバーレイ: 出すか (既定は出さない) と表示する秒数 (既定 10。0 なら出したまま)。位置・大きさ・文字の大きさ・透明度は、下の「オーバーレイの調整」の欄とボタンで変える。
//     位置と大きさは、オーバーレイの上で 左 Ctrl + 左 Shift + クリックでも変えられる (OreOverlayWindow)
public partial class MainWindow
{
    private readonly MapCurrentHotkey _mapHotkey = new();
    // キーの欄で押して入れた、まだ保存していないキー (無ければ null)
    private MapCurrentHotkey.Spec? _pendingMapHotkey;

    // Window_Loaded から
    private void InitOreOverlaySettings()
    {
        chkOreOverlay.IsChecked = App.Config.OreOverlayEnabled;
        txtOreOverlaySeconds.Text = App.Config.OreOverlaySeconds.ToString(CultureInfo.InvariantCulture);
        chkMapHotkey.IsChecked = App.Config.MapCurrentHotkeyEnabled;
        ShowMapHotkeyText();
        // キーが押されたら、［現在位置］ボタンと同じ処理 (ゲームの窓は前のまま)
        _mapHotkey.Pressed += () =>
        {
            if (_exiting) return;
            if (_mapHotkey.Registered is { } pressed) Log($"[現在位置のキー] 「{MapCurrentHotkey.Display(pressed)}」が押されました");
            _ = mapPanel.CaptureCurrentFromHotkeyAsync(_mapHotkey.AnyKeyDown);
        };
        // 主のキーは押されたが修飾キーが合わなかった (効かなかったときに、どこで止まったかをログで見るため。MapCurrentHotkey が 10 秒に 1 回までにしている)
        _mapHotkey.Ignored += why =>
        {
            if (_exiting || _mapHotkey.Registered is not { } want) return;
            Log($"[現在位置のキー] 主のキーは押されましたが、修飾キーが「{MapCurrentHotkey.Display(want)}」と合わないので何もしません ({why}。この知らせは 10 秒に 1 回まで)");
        };
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(_mapHotkey.WndProc);
        ApplyMapHotkey();
        // オーバーレイの調整の欄: 今の値を入れ、オーバーレイの側で変わったとき (マウスで動かした・閉じた・調整用にした) に合わせ直す
        OreOverlayWindow.OverlayChanged += ShowOreOverlayAdjustState;
        ShowOreOverlayAdjustState();
        _oreAdjustReady = true;
    }

    // ===== オーバーレイの調整 (2026-11-05、利用者の指示「大きさを変えるのは設定でやらないと、ゲーム中はクリックできない。設定で表示させ、アプリの設定画面のボタンで
    //  移動やサイズ変更、文字サイズ、透明度を変えないと使えない」) =====
    // ［オーバーレイを表示して調整］: 調整用に出す / 閉じる。位置 (左・上の欄と ←→↑↓)・大きさ (幅・高さの欄と ＋−)・文字の大きさ (欄と ＋−)・透明度 (スライダー)・［既定に戻す］。
    // どれも、変えたらすぐ設定に入れて保存し (SaveConfigToFile(showError: false))、出ているオーバーレイに当てる。オーバーレイをマウスで動かしたときは欄に出す (OverlayChanged)。
    // 欄の値は Enter か欄を離れたときに当てる。正しくない値は知らせて前の値に戻す。画面の外へ出る位置は OreOverlayWindow.EffectiveBounds の決まりで中へ戻る

    // ←→↑↓ と 幅・高さの ＋− が 1 回で動かす幅 (Shift を押しながらなら細かいほう)
    private const double OreAdjustStep = 10, OreAdjustFineStep = 1;
    // 文字の大きさの ＋− が 1 回で変える幅
    private const double OreFontStep = 1;
    private const string OreAdjustTitle = "現在位置のオーバーレイ";
    // 欄に初めの値を入れ終えたか (XAML の読み込み中のスライダーの ValueChanged を無視する)、欄を書き直している間か、調整用に出す準備をしている間か
    private bool _oreAdjustReady, _oreAdjustFilling, _oreAdjustStarting;

    private static string OreNum(double v) => Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture);

    // 欄とボタンの表示を今の状態に合わせる (位置・大きさは、出ている窓か、出ていなければ設定から決まる値)
    private void ShowOreOverlayAdjustState()
    {
        _oreAdjustFilling = true;
        try
        {
            var b = OreOverlayWindow.CurrentBounds();
            txtOreOverlayLeft.Text = OreNum(b.Left);
            txtOreOverlayTop.Text = OreNum(b.Top);
            txtOreOverlayWidth.Text = OreNum(b.Width);
            txtOreOverlayHeight.Text = OreNum(b.Height);
            txtOreOverlayFontSize.Text = OreNum(OreOverlayWindow.ConfigFontSize());
            sldOreOverlayOpacity.Value = OreOverlayWindow.ConfigOpacity();
            btnOreOverlayAdjust.Content = OreOverlayWindow.IsAdjusting ? "オーバーレイを閉じる (調整を終える)" : "オーバーレイを表示して調整";
        }
        finally { _oreAdjustFilling = false; }
    }

    // ［オーバーレイを表示して調整］: 出していれば閉じ、出していなければ調整用に出す (中身は MapPanel.OreOverlayContentForAdjustAsync)
    private async void OreOverlayAdjust_Click(object sender, RoutedEventArgs e)
    {
        if (OreOverlayWindow.IsAdjusting) { OreOverlayWindow.StopAdjust(); return; }
        if (_oreAdjustStarting) return;
        _oreAdjustStarting = true;
        try
        {
            var content = await mapPanel.OreOverlayContentForAdjustAsync();
            if (_exiting) return;
            OreOverlayWindow.StartAdjust(content, () => SaveConfigToFile(showError: false));
        }
        catch (Exception ex)
        {
            Log($"[オーバーレイ] 調整用に出せませんでした: {ex.GetType().Name}: {ex.Message}");
            MessageBox.Show(this, $"オーバーレイを出せませんでした: {ex.Message}", OreAdjustTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _oreAdjustStarting = false; }
    }

    // 位置・大きさを設定に入れ (画面の外・下限は OreOverlayWindow が直す)、出ているオーバーレイに当て、保存する (欄は OverlayChanged で合わせ直る)
    private void SetOreOverlayBounds(double left, double top, double width, double height)
    {
        OreOverlayWindow.SetBounds(left, top, width, height);
        SaveConfigToFile(showError: false);
    }

    private void SetOreOverlayFontSize(double size)
    {
        App.Config.OreOverlayFontSize = Math.Clamp(size, OreOverlayWindow.MinFontSize, OreOverlayWindow.MaxFontSize);
        OreOverlayWindow.ApplyAppearance();
        SaveConfigToFile(showError: false);
        ShowOreOverlayAdjustState();
    }

    private static double OreStepNow() => (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? OreAdjustFineStep : OreAdjustStep;

    // ←→↑↓ (Tag が L / R / U / D)
    private void OreOverlayMove_Click(object sender, RoutedEventArgs e)
    {
        var b = OreOverlayWindow.CurrentBounds();
        var d = OreStepNow();
        var (dx, dy) = ((sender as FrameworkElement)?.Tag as string) switch { "L" => (-d, 0.0), "R" => (d, 0.0), "U" => (0.0, -d), "D" => (0.0, d), _ => (0.0, 0.0) };
        SetOreOverlayBounds(b.Left + dx, b.Top + dy, b.Width, b.Height);
    }

    // 幅・高さの ＋− (Tag が W- / W+ / H- / H+)
    private void OreOverlaySize_Click(object sender, RoutedEventArgs e)
    {
        var b = OreOverlayWindow.CurrentBounds();
        var d = OreStepNow();
        var (dw, dh) = ((sender as FrameworkElement)?.Tag as string) switch { "W-" => (-d, 0.0), "W+" => (d, 0.0), "H-" => (0.0, -d), "H+" => (0.0, d), _ => (0.0, 0.0) };
        SetOreOverlayBounds(b.Left, b.Top, b.Width + dw, b.Height + dh);
    }

    // 文字の大きさの ＋− (Tag が - / +)
    private void OreOverlayFont_Click(object sender, RoutedEventArgs e) =>
        SetOreOverlayFontSize(OreOverlayWindow.ConfigFontSize() + (((sender as FrameworkElement)?.Tag as string) == "-" ? -OreFontStep : OreFontStep));

    // 透明度のスライダー
    private void OreOverlayOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_oreAdjustReady || _oreAdjustFilling) return;
        var v = Math.Clamp((int)Math.Round(e.NewValue), OreOverlayWindow.MinOpacity, OreOverlayWindow.MaxOpacity);
        if (v == App.Config.OreOverlayOpacity) return;
        App.Config.OreOverlayOpacity = v;
        OreOverlayWindow.ApplyAppearance();
        SaveConfigToFile(showError: false);
    }

    // ［既定に戻す］: 位置・大きさ・文字の大きさ・透明度
    private void OreOverlayReset_Click(object sender, RoutedEventArgs e)
    {
        OreOverlayWindow.ResetToDefault();
        SaveConfigToFile(showError: false);
        ShowOreOverlayAdjustState();
    }

    // 数値の欄 (左・上・幅・高さ・文字の大きさ): Enter か、欄を離れたときに当てる
    private void OreOverlayBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        CommitOreOverlayBox(sender as TextBox);
    }

    private void OreOverlayBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitOreOverlayBox(sender as TextBox);

    // 欄の値を当てる。今の値と同じ文字なら何もしない。数でない・範囲の外なら、知らせて前の値に戻す
    private void CommitOreOverlayBox(TextBox? box)
    {
        if (box == null || !_oreAdjustReady || _oreAdjustFilling) return;
        var b = OreOverlayWindow.CurrentBounds();
        var (name, current, min, max) =
            ReferenceEquals(box, txtOreOverlayLeft) ? ("オーバーレイの位置 (左)", b.Left, double.NegativeInfinity, double.PositiveInfinity)
            : ReferenceEquals(box, txtOreOverlayTop) ? ("オーバーレイの位置 (上)", b.Top, double.NegativeInfinity, double.PositiveInfinity)
            : ReferenceEquals(box, txtOreOverlayWidth) ? ("オーバーレイの幅", b.Width, OreOverlayWindow.MinOverlayWidth, double.PositiveInfinity)
            : ReferenceEquals(box, txtOreOverlayHeight) ? ("オーバーレイの高さ", b.Height, OreOverlayWindow.MinOverlayHeight, double.PositiveInfinity)
            : ("オーバーレイの文字の大きさ", OreOverlayWindow.ConfigFontSize(), OreOverlayWindow.MinFontSize, OreOverlayWindow.MaxFontSize);
        var text = box.Text.Trim();
        if (text == OreNum(current)) return;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v) || v < min || v > max)
        {
            // 先に前の値に戻す (知らせの窓で欄を離れたときに、もう一度ここへ来ても何もしないように)
            box.Text = OreNum(current);
            var range = double.IsInfinity(min) ? "数" : double.IsInfinity(max) ? $"数 ({OreNum(min)} 以上)" : $"数 ({OreNum(min)}〜{OreNum(max)})";
            MessageBox.Show(this, $"{name}「{text}」は{range}ではないので変えません。前の値 ({OreNum(current)}) のままです。", OreAdjustTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (ReferenceEquals(box, txtOreOverlayFontSize)) { SetOreOverlayFontSize(v); return; }
        SetOreOverlayBounds(ReferenceEquals(box, txtOreOverlayLeft) ? v : b.Left, ReferenceEquals(box, txtOreOverlayTop) ? v : b.Top,
            ReferenceEquals(box, txtOreOverlayWidth) ? v : b.Width, ReferenceEquals(box, txtOreOverlayHeight) ? v : b.Height);
    }

    // キーの欄に、設定のキーの画面の文字を出す
    private void ShowMapHotkeyText()
    {
        _pendingMapHotkey = null;
        txtMapHotkey.Text = MapCurrentHotkey.Parse(App.Config.MapCurrentHotkey) is { } spec ? MapCurrentHotkey.Display(spec) : "";
    }

    // 設定のとおりにキーを登録し直す (使わない・キーが読めないなら外すだけ)。結果を欄の横に出す (log が真ならログにも)
    private void ApplyMapHotkey(bool log = true)
    {
        _mapHotkey.Unregister();
        if (!App.Config.MapCurrentHotkeyEnabled)
        {
            txtMapHotkeyStatus.Text = "キーは使いません";
            return;
        }
        if (MapCurrentHotkey.Parse(App.Config.MapCurrentHotkey) is not { } spec)
        {
            txtMapHotkeyStatus.Text = string.IsNullOrWhiteSpace(App.Config.MapCurrentHotkey) ? "キーが決まっていません (欄を選んでキーを押してください)"
                : $"設定のキー「{App.Config.MapCurrentHotkey}」を読めないので使いません (欄を選んでキーを押してください)";
            if (log) Log($"[現在位置のキー] {txtMapHotkeyStatus.Text}");
            return;
        }
        var hwnd = new WindowInteropHelper(this).Handle;
        var name = MapCurrentHotkey.Display(spec);
        var error = 0;
        if (hwnd != IntPtr.Zero && _mapHotkey.Register(hwnd, spec, out error))
        {
            txtMapHotkeyStatus.Text = $"「{name}」で現在位置を取ります";
            if (log) Log($"[現在位置のキー] 「{name}」を登録しました (Raw Input)");
        }
        else
        {
            // 登録の失敗は、ログを出さない呼び出しでもログに残す
            txtMapHotkeyStatus.Text = $"「{name}」を登録できませんでした (キーボードの Raw Input の登録の失敗。エラー {error})";
            Log($"[現在位置のキー] 「{name}」を登録できませんでした (キーボードの Raw Input の登録の失敗。エラー {error})");
        }
    }

    // キーの欄: 選んでいる間は今のキーの登録を外し (今のキーを押したときも、取り込みを始めずに欄に入るように)、外れたら設定のとおりに登録し直す
    private void TxtMapHotkey_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _mapHotkey.Unregister();
        txtMapHotkeyStatus.Text = "割り当てるキーを押してください (例: 左 Alt + 左 Shift + Insert)";
    }

    private void TxtMapHotkey_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        ApplyMapHotkey(log: false);
        if (_pendingMapHotkey != null) txtMapHotkeyStatus.Text = "キーを変えました。「設定を保存」で有効になります";
    }

    // キーの欄でキーを押した: 修飾キー (左右別) と主のキーを欄に入れる (まだ保存しない)。Tab だけは欄の移動に使うので受けない
    private void TxtMapHotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (key == Key.Tab && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift) return;
        e.Handled = true;
        if (key == Key.None || key == Key.DeadCharProcessed || MapCurrentHotkey.IsModifierKey(key)) return;   // 修飾キーだけの間は待つ
        var spec = new MapCurrentHotkey.Spec(
            Keyboard.IsKeyDown(Key.LeftCtrl), Keyboard.IsKeyDown(Key.RightCtrl),
            Keyboard.IsKeyDown(Key.LeftShift), Keyboard.IsKeyDown(Key.RightShift),
            Keyboard.IsKeyDown(Key.LeftAlt), Keyboard.IsKeyDown(Key.RightAlt), key);
        if (MapCurrentHotkey.Validate(spec) is { } why)
        {
            txtMapHotkeyStatus.Text = why;
            return;
        }
        _pendingMapHotkey = spec;
        txtMapHotkey.Text = MapCurrentHotkey.Display(spec);
        txtMapHotkeyStatus.Text = "「設定を保存」で有効になります";
    }

    // 「設定を保存」から: キー (使う / 使わない・押して入れたキー) とオーバーレイ (出すか・表示する秒数)。
    // 秒数は 0 以上の整数だけ保存する (そうでなければ保存せずに知らせ、前の値のまま)
    private void SaveOreOverlaySettings()
    {
        App.Config.MapCurrentHotkeyEnabled = chkMapHotkey.IsChecked == true;
        if (_pendingMapHotkey is { } spec) App.Config.MapCurrentHotkey = MapCurrentHotkey.ToConfigText(spec);
        ShowMapHotkeyText();
        ApplyMapHotkey();

        var text = txtOreOverlaySeconds.Text.Trim();
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) || seconds < 0)
        {
            MessageBox.Show(this, $"オーバーレイを表示する秒数「{text}」は数 (0 以上の整数。0 なら出したまま) ではないので保存しません。前の値 ({App.Config.OreOverlaySeconds}) のままです。",
                "現在位置のオーバーレイ", MessageBoxButton.OK, MessageBoxImage.Warning);
            txtOreOverlaySeconds.Text = App.Config.OreOverlaySeconds.ToString(CultureInfo.InvariantCulture);
        }
        else App.Config.OreOverlaySeconds = seconds;
        App.Config.OreOverlayEnabled = chkOreOverlay.IsChecked == true;
        // オフにしたら、出ているオーバーレイを閉じる (調整用に出しているものは、オフでも出したまま)
        if (!App.Config.OreOverlayEnabled && !OreOverlayWindow.IsAdjusting) OreOverlayWindow.CloseIfOpen();
    }
}
