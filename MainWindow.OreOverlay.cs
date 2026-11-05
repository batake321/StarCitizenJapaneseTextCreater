using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace StarCitizenJapaneseTextCreater;

// 設定タブの「現在位置のキーとオーバーレイ」の欄と、現在位置のキー (MapCurrentHotkey) の登録。
//   キー: ［マップ］の［現在位置］ボタンと同じ処理を、ゲームが前にあるままキーで動かす (MapPanel.CaptureCurrentFromHotkeyAsync)。
//     使う / 使わないとキーは設定 (App.Config.MapCurrentHotkeyEnabled / MapCurrentHotkey)。キーの欄を選んでキーを押すと、押したキー (修飾キーは左右別) が入る。
//     「設定を保存」で有効になる。登録できなかったとき (ほかのアプリが同じキーを使っているなど) は、欄の横の文とログで知らせる (例外にしない。PrintScreen の登録の失敗と同じ扱い)。
//   オーバーレイ: 出すか (既定は出さない) と表示する秒数 (既定 10。0 なら出したまま)。位置と大きさは、オーバーレイの上で 左 Ctrl + 左 Shift + クリックで変える (OreOverlayWindow)
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
        _mapHotkey.Pressed += () => { if (!_exiting) _ = mapPanel.CaptureCurrentFromHotkeyAsync(_mapHotkey.AnyKeyDown); };
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(_mapHotkey.WndProc);
        ApplyMapHotkey();
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
            if (log) Log($"[現在位置のキー] 「{name}」を登録しました");
        }
        else
        {
            // 登録の失敗は、ログを出さない呼び出しでもログに残す (ほかのアプリが同じキーを使っているなど)
            txtMapHotkeyStatus.Text = $"「{name}」を登録できませんでした (ほかのアプリが同じキーを使っている可能性があります)";
            Log($"[現在位置のキー] 「{name}」を登録できませんでした (ほかのアプリが同じキーを使っている可能性があります。エラー {error})");
        }
    }

    // キーの欄: 選んでいる間は今のキーの登録を外し (今のキーを押したときも、取り込みを始めずに欄に入るように)、外れたら設定のとおりに登録し直す
    private void TxtMapHotkey_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _mapHotkey.Unregister();
        txtMapHotkeyStatus.Text = "割り当てるキーを押してください (例: 左 Ctrl + 左 Shift + Insert)";
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
        // オフにしたら、出ているオーバーレイを閉じる
        if (!App.Config.OreOverlayEnabled) OreOverlayWindow.CloseIfOpen();
    }
}
