using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace StarCitizenJapaneseTextCreater;

// 現在位置のオーバーレイ: ［マップ］の［現在位置］(ボタン・キー・ステーションの 3D の［現在位置］) で座標が取れたときに、その場所で採掘できる物 (OreHere.Content) をゲームの画面の上に出す窓。
// (2026-11-05、利用者の指示「マップの現在位置ボタンを押したときに、ゲームの画面にオーバーレイ表示するようにして、デフォルトはオフで設定で変えられるように。
//  表示時間は出っぱなしから一定時間で消えるまで。デフォルトで 10 秒。CTRL + オーバーレイクリックで位置や大きさを変えられ、変更後クリックで確定」と、
//  「L-CTRL + L-Shift でやらないと 所持品ストレージの所持品の移動が CTRL+LMB」)
//   窓: 枠なし・半透明の暗い背景に明るい文字・最前面 (Topmost)・タスクバーに出さない・フォーカスを取らない (ShowActivated = false と WS_EX_NOACTIVATE、
//     WM_MOUSEACTIVATE に MA_NOACTIVATE)・Owner 無し。窓は 1 つだけ (s_window)。もう一度出すと中身を入れ替える。アプリの終了時は CloseIfOpen と
//     主の窓の Closed で閉じる (Owner の無い窓が残ると、主の窓を閉じてもアプリが終わらないため)。
//   中身: 設定の文字の大きさ (App.Config.OreOverlayFontSize) で組み、行は窓の幅で折り返す。組んだ中身が窓の高さに収まらないときだけ、全部が見えるように全体を縮める
//     (切らない。大きくはしない。FitBox)。窓全体の不透明さは設定の透明度 (App.Config.OreOverlayOpacity。% で、100 が今までの見かけ)。
//     (2026-11-05、利用者の指示「鉱石のオーバーレイ 文字が小さすぎる。あまりにも 4K だから特に 大きさを変えるのは設定でやらないと、ゲーム中はクリックできない。
//      設定で表示させ、アプリの設定画面のボタンで移動やサイズ変更、文字サイズ、透明度を変えないと使えない」)
//   調整用 (設定タブの［オーバーレイを表示して調整］。StartAdjust / StopAdjust): 出している間は時間で消えず、修飾キー無しでマウスを受ける (枠・つまみ・✕ を出す。
//     ドラッグで移動、端・角のドラッグで大きさの変更。離したときに設定に入れてすぐ保存する)。動かさないクリックは何もしない。✕ は窓を閉じる (調整を終える)。
//     調整用に出している間に現在位置を取ったときは、中身を入れ替えるだけ (調整用のまま)。設定タブの欄・ボタンからは SetBounds / ApplyAppearance / ResetToDefault で変える。
//     位置・大きさ・調整用かどうかが変わったら OverlayChanged で知らせる (設定タブの欄とボタンの表示を合わせるため)。
//   クリック: ふだんは下 (ゲーム) へ通す (WS_EX_TRANSPARENT)。左 Ctrl と左 Shift を両方押している間だけ受ける (ModifierPollMs ごとに
//     GetAsyncKeyState(VK_LCONTROL) と GetAsyncKeyState(VK_LSHIFT) の最上位ビットを見て、拡張スタイルを切り替える。表示中だけ動くタイマー)。
//     Ctrl だけ・Shift だけ・右 Ctrl・右 Shift のときは通す (ゲームの所持品の移動が Ctrl + 左クリックのため)。
//   変更中: 左 Ctrl + 左 Shift + クリックで入る。枠・つまみ・✕ を出し、クリックを通さず、時間で消えない。ドラッグで移動、端・角のドラッグで大きさの変更。
//     動かさないクリック (ドラッグにならなかったクリック。修飾キーは求めない) で確定: 位置と大きさを設定に入れてすぐ保存し、クリックを通す状態に戻し、消えるまでの時間を数え直す。
//     ✕ (変更中だけ出る。修飾キーは求めない) は、確定せずに (設定は変更前の位置・大きさのまま) 窓を閉じる。
//   表示時間: 設定の秒数 (App.Config.OreOverlaySeconds。0 なら出したまま) で閉じる。もう一度出すと数え直す。
//   撮影: SetWindowDisplayAffinity の WDA_EXCLUDEFROMCAPTURE を付けて、アプリの 2 つの撮影 (PrintScreen の価格キャプチャ・［現在位置］の前の mobiGlas の確認) に
//     写らないようにする。Windows 10 2004 (build 19041) より前では付けない (その版では WDA_MONITOR と同じ扱いになり、撮った絵に黒い枠が出るため。
//     SetWindowDisplayAffinity の説明)。付けられなくても例外にしない (アプリのログに 1 回出す)
internal sealed class OreOverlayWindow : Window
{
    // 位置・大きさを決めたことが無い間の大きさ (Config.cs の既定) と、大きさの下限・主画面の端からの間 (WPF の単位)
    // (既定の大きさは、既定の文字の大きさ 20 で Yela の中身 (岩 16 行 + 品質 6 行) が縮まずに入る大きさ。確認用の描画で、幅 1416 のとき中身の高さは約 1150)
    public const double DefaultWidth = 1440, DefaultHeight = 1200;
    public const double MinOverlayWidth = 200, MinOverlayHeight = 120;
    private const double ScreenMargin = 16;
    // 文字の大きさ (WPF の単位) の既定と範囲、透明度 (窓全体の不透明さ。%) の既定と範囲
    public const double DefaultFontSize = 20, MinFontSize = 8, MaxFontSize = 72;
    public const int DefaultOpacity = 100, MinOpacity = 20, MaxOpacity = 100;
    // 中身を組む幅の下限 (文字の大きさの何倍か)。窓がこれより狭いときは、この幅で組んでから全体を縮める (表の左の 4 つの列が切れないように)
    private const double MinContentEm = 44;
    // 中身の間 (余白) を決めたときの文字の大きさ (余白は文字の大きさに比例させる)
    private const double BaseFontSize = 14;
    // 変更中に、端・角とみなす幅と、つまみの大きさ
    private const double EdgeBand = 12, HandleSize = 10;
    // 修飾キーの状態を見る間隔 (ms)
    private const int ModifierPollMs = 50;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
    private const int VK_LSHIFT = 0xA0, VK_LCONTROL = 0xA2;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
    private const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;
    // WDA_EXCLUDEFROMCAPTURE が使える最初の版 (Windows 10 Version 2004)
    private const int ExcludeFromCaptureMinBuild = 19041;

    private static OreOverlayWindow? s_window;
    private static bool s_affinityFailureLogged;

    // 位置・大きさ (マウスで動かした・設定タブから変えた・確定した)、調整用かどうか、窓が開いているかが変わった
    public static event Action? OverlayChanged;

    // 調整用に出しているか (設定タブの［オーバーレイを表示して調整］)
    public static bool IsAdjusting => s_window is { _adjusting: true };

    // 設定の文字の大きさ (範囲の外・数でない値は既定) と透明度 (範囲に収める)
    public static double ConfigFontSize() => App.Config.OreOverlayFontSize is var f && double.IsFinite(f) && f >= MinFontSize && f <= MaxFontSize ? f : DefaultFontSize;
    public static int ConfigOpacity() => Math.Clamp(App.Config.OreOverlayOpacity, MinOpacity, MaxOpacity);

    private static OreOverlayWindow GetOrCreate(Action? saveConfig)
    {
        var w = s_window;
        if (w == null)
        {
            w = new OreOverlayWindow(saveConfig);
            s_window = w;
            w.Closed += (_, _) => { if (ReferenceEquals(s_window, w)) s_window = null; OverlayChanged?.Invoke(); };
        }
        w._saveConfig = saveConfig;
        return w;
    }

    // 中身を出す (窓が無ければ作る。あれば中身を入れ替え、消えるまでの時間を数え直す。調整用に出している間は中身を入れ替えるだけ)。
    // saveConfig: 位置・大きさを決めたときに設定を保存する (MainWindow.SaveConfigToFile)
    public static void ShowContent(OreHere.Content content, Action? saveConfig)
    {
        var w = GetOrCreate(saveConfig);
        w.SetContent(content);
        if (!w.IsVisible)
        {
            w.ApplyBounds();
            w.Show();
        }
        w.RestartDismissTimer();
    }

    // 調整用に出す (設定タブの［オーバーレイを表示して調整］): 中身を出し、時間で消えず、修飾キー無しでマウスを受ける状態にする
    public static void StartAdjust(OreHere.Content content, Action? saveConfig)
    {
        var w = GetOrCreate(saveConfig);
        w.SetContent(content);
        if (!w.IsVisible)
        {
            w.ApplyBounds();
            w.Show();
        }
        w._adjusting = true;
        w.EnterEdit();
        OverlayChanged?.Invoke();
    }

    // 調整用に出していれば閉じる (もう一度ボタンを押したとき)
    public static void StopAdjust()
    {
        if (IsAdjusting) CloseIfOpen();
    }

    // 開いていれば閉じる (アプリの終了時・設定でオフにしたとき)
    public static void CloseIfOpen()
    {
        try { s_window?.Close(); } catch { }
    }

    // 今の位置と大きさ: 窓が出ていればその窓、出ていなければ設定から決まる値 (EffectiveBounds)
    public static Rect CurrentBounds() =>
        s_window is { IsVisible: true } w && double.IsFinite(w.Left) && double.IsFinite(w.Top) ? new Rect(w.Left, w.Top, w.Width, w.Height) : EffectiveBounds();

    // 設定タブから位置・大きさを変える: 設定に入れ (画面の外・下限より小さい値は EffectiveBounds の決まりで直す)、窓が出ていればすぐ当てる。保存は呼ぶ側
    public static void SetBounds(double left, double top, double width, double height)
    {
        var c = App.Config;
        c.OreOverlayLeft = left;
        c.OreOverlayTop = top;
        c.OreOverlayWidth = Math.Max(MinOverlayWidth, width);
        c.OreOverlayHeight = Math.Max(MinOverlayHeight, height);
        c.OreOverlayPlaced = true;
        var b = EffectiveBounds();
        c.OreOverlayLeft = b.Left;
        c.OreOverlayTop = b.Top;
        c.OreOverlayWidth = b.Width;
        c.OreOverlayHeight = b.Height;
        if (s_window is { IsVisible: true } w) w.ApplyBounds();
        OverlayChanged?.Invoke();
    }

    // 設定の文字の大きさ・透明度を、出ている窓にすぐ当てる (文字の大きさが変わっていれば中身を組み直す)
    public static void ApplyAppearance()
    {
        if (s_window is not { } w) return;
        w.Opacity = ConfigOpacity() / 100.0;
        if (w._lastContent is { } content && w._font != ConfigFontSize()) w.SetContent(content);
    }

    // 位置・大きさ・文字の大きさ・透明度を既定に戻す (設定に入れ、窓が出ていればすぐ当てる。保存は呼ぶ側)
    public static void ResetToDefault()
    {
        var c = App.Config;
        c.OreOverlayLeft = 0;
        c.OreOverlayTop = 0;
        c.OreOverlayWidth = DefaultWidth;
        c.OreOverlayHeight = DefaultHeight;
        c.OreOverlayPlaced = false;
        c.OreOverlayFontSize = DefaultFontSize;
        c.OreOverlayOpacity = DefaultOpacity;
        if (s_window is { IsVisible: true } w) w.ApplyBounds();
        ApplyAppearance();
        OverlayChanged?.Invoke();
    }

    private Action? _saveConfig;
    private readonly Grid _root = new();
    private readonly StackPanel _content = new();
    private readonly FitBox _fit = new();
    // 今出している中身 (文字の大きさを変えたときに組み直すため) と、その中身を組んだ文字の大きさ
    private OreHere.Content? _lastContent;
    private double _font = DefaultFontSize;
    private readonly Border _editFrame;
    private readonly Grid _handles = new() { Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Border _closeBox;
    private readonly DispatcherTimer _modifierTimer = new() { Interval = TimeSpan.FromMilliseconds(ModifierPollMs) };
    private readonly DispatcherTimer _dismissTimer = new();
    private IntPtr _hwnd;
    private bool _clickThrough;
    // 変更中か (枠・つまみを出してマウスを受けている。調整用のときも真)、調整用に出しているか (設定タブから。真の間は時間で消えず、動かさないクリックで確定しない)、
    // 変更中のドラッグ (押している・動かした・どこを掴んだ・押した所 (画面)・押したときの窓)
    private bool _editing, _adjusting;
    private enum Zone { Move, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }
    private bool _pressed, _dragged;
    private Zone _zone;
    private Point _pressScreen;
    private Rect _pressBounds;

    private static readonly Brush TextBrush = Frozen(Color.FromRgb(0xF2, 0xF4, 0xF6));
    private static readonly Brush DimBrush = Frozen(Color.FromRgb(0xA9, 0xB7, 0xC6));
    private static readonly Brush AccentBrush = Frozen(Color.FromRgb(0xFF, 0xD5, 0x4F));
    private static readonly Brush PanelBrush = Frozen(Color.FromArgb(0xD0, 0x10, 0x16, 0x1E));
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private OreOverlayWindow(Action? saveConfig)
    {
        _saveConfig = saveConfig;
        Title = "現在位置のオーバーレイ";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontFamily = new FontFamily("Yu Gothic UI, Meiryo UI, Segoe UI");
        FontSize = 14;
        Opacity = ConfigOpacity() / 100.0;

        // 背景 (半透明の暗い色) と中身 (設定の文字の大きさで組み、窓の幅で折り返す。高さに収まらないときだけ縮める)
        _fit.Child = _content;
        _root.Children.Add(new Border
        {
            Background = PanelBrush, CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 10, 12, 10),
            Child = _fit,
        });
        // 変更中の枠・つまみ (角と辺の真ん中)・✕
        _editFrame = new Border { BorderBrush = AccentBrush, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(6), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        _root.Children.Add(_editFrame);
        foreach (var (h, v) in new[]
                 {
                     (HorizontalAlignment.Left, VerticalAlignment.Top), (HorizontalAlignment.Center, VerticalAlignment.Top), (HorizontalAlignment.Right, VerticalAlignment.Top),
                     (HorizontalAlignment.Left, VerticalAlignment.Center), (HorizontalAlignment.Right, VerticalAlignment.Center),
                     (HorizontalAlignment.Left, VerticalAlignment.Bottom), (HorizontalAlignment.Center, VerticalAlignment.Bottom), (HorizontalAlignment.Right, VerticalAlignment.Bottom),
                 })
            _handles.Children.Add(new Rectangle { Width = HandleSize, Height = HandleSize, Fill = AccentBrush, HorizontalAlignment = h, VerticalAlignment = v });
        _root.Children.Add(_handles);
        _closeBox = new Border
        {
            Width = 26, Height = 26, Margin = new Thickness(0, HandleSize + 4, HandleSize + 4, 0), CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Background = Frozen(Color.FromRgb(0xC6, 0x28, 0x28)), Visibility = Visibility.Collapsed, Cursor = Cursors.Hand,
            Child = new TextBlock { Text = "✕", Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        // ✕: 確定せずに閉じる (設定の位置・大きさは変更前のまま)。窓のドラッグの処理へは渡さない
        _closeBox.MouseLeftButtonDown += (_, e) => e.Handled = true;
        _closeBox.MouseLeftButtonUp += (_, e) => { e.Handled = true; Close(); };
        _root.Children.Add(_closeBox);
        Content = _root;

        _root.MouseLeftButtonDown += Root_MouseLeftButtonDown;
        _root.MouseMove += Root_MouseMove;
        _root.MouseLeftButtonUp += Root_MouseLeftButtonUp;
        _root.LostMouseCapture += (_, _) => _pressed = false;

        _modifierTimer.Tick += (_, _) => UpdateClickThrough();
        _dismissTimer.Tick += (_, _) =>
        {
            _dismissTimer.Stop();
            if (!_editing && !_adjusting) Close();   // 変更中・調整用は時間で消さない (確定したときに数え直す)
        };
        SourceInitialized += OnSourceInitialized;
        IsVisibleChanged += (_, _) => { if (IsVisible) _modifierTimer.Start(); else _modifierTimer.Stop(); };
        // 主の窓が閉じたら閉じる (Owner の無い窓が残って、アプリが終わらないことが無いように)
        var main = Application.Current?.MainWindow;
        EventHandler? onMainClosed = null;
        onMainClosed = (_, _) => Close();
        if (main != null && !ReferenceEquals(main, this)) main.Closed += onMainClosed;
        Closed += (_, _) =>
        {
            _modifierTimer.Stop();
            _dismissTimer.Stop();
            if (main != null) main.Closed -= onMainClosed;
        };
    }

    // 窓ができたとき: フォーカスを取らない・Alt+Tab に出さない・クリックを通す拡張スタイルと、撮影に写らない印を付ける
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);
        _clickThrough = true;
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        try
        {
            if (Environment.OSVersion.Version.Build >= ExcludeFromCaptureMinBuild && !SetWindowDisplayAffinity(_hwnd, WDA_EXCLUDEFROMCAPTURE))
                LogAffinityFailure($"SetWindowDisplayAffinity が失敗しました (エラー {Marshal.GetLastWin32Error()})");
        }
        catch (Exception ex2) { LogAffinityFailure($"{ex2.GetType().Name}: {ex2.Message}"); }
    }

    private static void LogAffinityFailure(string why)
    {
        if (s_affinityFailureLogged) return;
        s_affinityFailureLogged = true;
        App.AppendStartupLog(App.Config.WorkingDirectory, $"[オーバーレイ] 撮影に写らない印を付けられませんでした (価格キャプチャ・mobiGlas の確認に写ることがあります): {why}");
    }

    // クリックされても窓を前に出さない (ゲームからフォーカスを取らない)
    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_MOUSEACTIVATE) { handled = true; return new IntPtr(MA_NOACTIVATE); }
        return IntPtr.Zero;
    }

    // 左 Ctrl と左 Shift が両方押されているか (GetAsyncKeyState の戻り値の最上位ビット = 今押されている。VK_LCONTROL = 0xA2、VK_LSHIFT = 0xA0。
    // 右 Ctrl (0xA3)・右 Shift (0xA1) は見ないので、右のキーでは押されていない扱い)
    private static bool LeftCtrlShiftDown() => (GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0 && (GetAsyncKeyState(VK_LSHIFT) & 0x8000) != 0;

    // クリックを通すか決め直す: 変更中 (調整用を含む) は通さない。そうでなければ、左 Ctrl + 左 Shift を押している間だけ受け、ほかは通す
    private void UpdateClickThrough()
    {
        if (_hwnd == IntPtr.Zero) return;
        var through = !_editing && !LeftCtrlShiftDown();
        if (through == _clickThrough) return;
        var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, through ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT);
        _clickThrough = through;
    }

    // 出すときの位置と大きさ: 設定の値。位置・大きさを決めたことが無ければ (OreOverlayPlaced が偽)、既定の大きさで主画面の作業領域の右上
    // (決めたことが無い間は、設定に残っている大きさ (前の版の既定 760×520) を使わない。大きさを変えられるのは決めたときだけで、そのとき OreOverlayPlaced が真になる)。
    // 画面 (全部の画面を合わせた範囲) の外になるときは中へ戻す
    public static Rect EffectiveBounds()
    {
        var c = App.Config;
        double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop, vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
        var w = c.OreOverlayPlaced && double.IsFinite(c.OreOverlayWidth) && c.OreOverlayWidth >= MinOverlayWidth ? c.OreOverlayWidth : DefaultWidth;
        var h = c.OreOverlayPlaced && double.IsFinite(c.OreOverlayHeight) && c.OreOverlayHeight >= MinOverlayHeight ? c.OreOverlayHeight : DefaultHeight;
        w = Math.Min(w, vw);
        h = Math.Min(h, vh);
        double left, top;
        if (c.OreOverlayPlaced && double.IsFinite(c.OreOverlayLeft) && double.IsFinite(c.OreOverlayTop))
        {
            left = c.OreOverlayLeft;
            top = c.OreOverlayTop;
        }
        else
        {
            var wa = SystemParameters.WorkArea;
            left = wa.Right - w - ScreenMargin;
            top = wa.Top + ScreenMargin;
        }
        // 画面の外に出ていれば中へ (RestoreWindowState と同じく、全部の画面を合わせた範囲に収まっているかで見る)
        left = Math.Clamp(left, vl, Math.Max(vl, vl + vw - w));
        top = Math.Clamp(top, vt, Math.Max(vt, vt + vh - h));
        return new Rect(left, top, w, h);
    }

    private void ApplyBounds()
    {
        var b = EffectiveBounds();
        Width = b.Width;
        Height = b.Height;
        Left = b.Left;
        Top = b.Top;
    }

    // 消えるまでの時間を数え直す (0 なら出したまま。変更中・調整用は数えない)
    private void RestartDismissTimer()
    {
        _dismissTimer.Stop();
        var seconds = App.Config.OreOverlaySeconds;
        if (seconds <= 0 || _editing || _adjusting) return;
        _dismissTimer.Interval = TimeSpan.FromSeconds(seconds);
        _dismissTimer.Start();
    }

    // ===== 中身 =====

    // 文字 1 つ。size: 文字の大きさが BaseFontSize (14) のときの大きさ (設定の文字の大きさに比例させる)。余白も同じ (M)
    private TextBlock Text(string text, Brush? brush = null, double size = BaseFontSize, bool bold = false, Thickness? margin = null) => new()
    {
        Text = text, Foreground = brush ?? TextBrush, FontSize = size * _font / BaseFontSize, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0),
    };

    // 余白 (文字の大きさが BaseFontSize のときの値) を、設定の文字の大きさに比例させる
    private Thickness M(double left, double top, double right, double bottom)
    {
        var k = _font / BaseFontSize;
        return new Thickness(left * k, top * k, right * k, bottom * k);
    }

    private void SetContent(OreHere.Content c)
    {
        _lastContent = c;
        _font = ConfigFontSize();
        _fit.MinChildWidth = MinContentEm * _font;
        _content.Children.Clear();
        _content.Children.Add(Text(c.Title, size: 20, bold: true));
        if (c.Message.Length > 0) _content.Children.Add(Text(c.Message, margin: M(0, 6, 0, 0)));
        // 近くの採掘の場所 (2026-11-06、利用者の指示「今いるとこ近辺提示しないと意味ない」「QT できるポイント名でないとわからない」。どの段にも当たらないときだけ):
        // 見出しの 1 行と、1 か所 1 行 (名前 | 距離 | 岩の名前。最後の列は折り返す)。品質の行と同じ描き方
        if (c.Near is { Count: > 0 } near)
        {
            _content.Children.Add(Text(OreHere.NearHeader, AccentBrush, bold: true, margin: M(0, 6, 0, 0)));
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < near.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var name = Text(near[i].Name, bold: true, margin: M(0, 2, 14, 0));
                var distance = Text(near[i].Distance, margin: M(0, 2, 14, 0));
                distance.HorizontalAlignment = HorizontalAlignment.Right;
                var rocks = Text(near[i].Rocks, margin: M(0, 2, 0, 0));
                Grid.SetRow(name, i);
                Grid.SetRow(distance, i);
                Grid.SetRow(rocks, i);
                Grid.SetColumn(distance, 1);
                Grid.SetColumn(rocks, 2);
                grid.Children.Add(name);
                grid.Children.Add(distance);
                grid.Children.Add(rocks);
            }
            _content.Children.Add(grid);
        }
        if (c.Sections.Count > 0)
        {
            // 表: 岩 | 格 | 出やすさ | シグネチャー | 岩の中の資源と割合 (最後の列は折り返す)
            var grid = new Grid { Margin = M(0, 6, 0, 0) };
            for (int i = 0; i < OreHere.Columns.Length; i++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = i == OreHere.Columns.Length - 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            int row = 0;
            void Cell(string text, int col, Brush? brush = null, bool bold = false, bool right = false, int span = 1, double top = 2)
            {
                var t = Text(text, brush, bold: bold, margin: M(0, top, col + span < OreHere.Columns.Length ? 14 : 0, 0));
                if (right) t.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetRow(t, row);
                Grid.SetColumn(t, col);
                if (span > 1) Grid.SetColumnSpan(t, span);
                grid.Children.Add(t);
            }
            void NextRow() { grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); }
            NextRow();
            for (int i = 0; i < OreHere.Columns.Length; i++) Cell(OreHere.Columns[i], i, DimBrush, right: i is 2 or 3);
            row++;
            foreach (var s in c.Sections)
            {
                NextRow();
                Cell(s.Method, 0, AccentBrush, bold: true, span: OreHere.Columns.Length, top: 8);
                row++;
                foreach (var r in s.Rocks)
                {
                    NextRow();
                    Cell(r.Rock, 0, bold: true);
                    Cell(r.Grade, 1);
                    Cell(r.Chance, 2, right: true);
                    Cell(r.Signature, 3, right: true);
                    Cell(r.Resources, 4);
                    row++;
                }
            }
            _content.Children.Add(grid);
        }
        if (c.Quality.Count > 0)
        {
            _content.Children.Add(Text(OreHere.QualityHeader, AccentBrush, bold: true, margin: M(0, 10, 0, 0)));
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < c.Quality.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var range = Text(c.Quality[i].Range, bold: true, margin: M(0, 2, 14, 0));
                var names = Text(c.Quality[i].Resources, margin: M(0, 2, 0, 0));
                Grid.SetRow(range, i);
                Grid.SetRow(names, i);
                Grid.SetColumn(names, 1);
                grid.Children.Add(range);
                grid.Children.Add(names);
            }
            _content.Children.Add(grid);
        }
    }

    // ===== 変更中 (位置・大きさ) =====

    private void EnterEdit()
    {
        _editing = true;
        _dismissTimer.Stop();
        _editFrame.Visibility = Visibility.Visible;
        _handles.Visibility = Visibility.Visible;
        _closeBox.Visibility = Visibility.Visible;
        UpdateClickThrough();
    }

    private void ExitEdit()
    {
        _editing = false;
        _adjusting = false;
        _pressed = false;
        _editFrame.Visibility = Visibility.Collapsed;
        _handles.Visibility = Visibility.Collapsed;
        _closeBox.Visibility = Visibility.Collapsed;
        _root.Cursor = null;
        UpdateClickThrough();
    }

    // 今の位置と大きさを設定に入れてすぐ保存する
    private void StoreBounds()
    {
        if (!double.IsFinite(Left) || !double.IsFinite(Top) || !double.IsFinite(Width) || !double.IsFinite(Height)) return;
        var c = App.Config;
        c.OreOverlayLeft = Left;
        c.OreOverlayTop = Top;
        c.OreOverlayWidth = Width;
        c.OreOverlayHeight = Height;
        c.OreOverlayPlaced = true;
        try { _saveConfig?.Invoke(); }
        catch (Exception ex) { App.AppendStartupLog(App.Config.WorkingDirectory, $"[オーバーレイ] 位置と大きさを保存できませんでした: {ex.Message}"); }
    }

    // 確定: 今の位置と大きさを設定に入れてすぐ保存し、クリックを通す状態に戻し、消えるまでの時間を数え直す
    private void Confirm()
    {
        StoreBounds();
        ExitEdit();
        RestartDismissTimer();
        OverlayChanged?.Invoke();
    }

    // マウスの今の位置 (画面。WPF の単位)
    private Point CursorPosition()
    {
        if (!GetCursorPos(out var p)) return _pressScreen;
        var pt = new Point(p.X, p.Y);
        return PresentationSource.FromVisual(this)?.CompositionTarget is { } ct ? ct.TransformFromDevice.Transform(pt) : pt;
    }

    // 窓の中の位置 → 掴む所 (端・角から EdgeBand 以内なら大きさの変更、ほかは移動)
    private Zone ZoneAt(Point p)
    {
        bool l = p.X <= EdgeBand, r = p.X >= _root.ActualWidth - EdgeBand, t = p.Y <= EdgeBand, b = p.Y >= _root.ActualHeight - EdgeBand;
        return t && l ? Zone.TopLeft : t && r ? Zone.TopRight : b && l ? Zone.BottomLeft : b && r ? Zone.BottomRight
            : l ? Zone.Left : r ? Zone.Right : t ? Zone.Top : b ? Zone.Bottom : Zone.Move;
    }

    private static Cursor CursorOf(Zone z) => z switch
    {
        Zone.TopLeft or Zone.BottomRight => Cursors.SizeNWSE,
        Zone.TopRight or Zone.BottomLeft => Cursors.SizeNESW,
        Zone.Left or Zone.Right => Cursors.SizeWE,
        Zone.Top or Zone.Bottom => Cursors.SizeNS,
        _ => Cursors.SizeAll,
    };

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_editing)
        {
            // 変更中に入るのは 左 Ctrl + 左 Shift + クリック (ふだんはクリックが届かないが、切り替えの間に届いた分もここで確かめる)。
            // このクリックの離しは確定にしない (_pressed を立てない)
            if (LeftCtrlShiftDown()) EnterEdit();
            return;
        }
        _pressed = true;
        _dragged = false;
        _zone = ZoneAt(e.GetPosition(_root));
        _pressScreen = CursorPosition();
        _pressBounds = new Rect(Left, Top, Width, Height);
        _root.CaptureMouse();
    }

    private void Root_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_editing) return;
        if (!_pressed) { _root.Cursor = CursorOf(ZoneAt(e.GetPosition(_root))); return; }
        var cur = CursorPosition();
        double dx = cur.X - _pressScreen.X, dy = cur.Y - _pressScreen.Y;
        // ドラッグとみなすのは、Windows の決まりの距離を超えて動いてから (それまでは「動かさないクリック」)
        if (!_dragged && Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(dy) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragged = true;
        var b = _pressBounds;
        double left = b.Left, top = b.Top, width = b.Width, height = b.Height;
        if (_zone == Zone.Move) { left += dx; top += dy; }
        if (_zone is Zone.Right or Zone.TopRight or Zone.BottomRight) width = Math.Max(MinOverlayWidth, b.Width + dx);
        if (_zone is Zone.Left or Zone.TopLeft or Zone.BottomLeft) { width = Math.Max(MinOverlayWidth, b.Width - dx); left = b.Right - width; }
        if (_zone is Zone.Bottom or Zone.BottomLeft or Zone.BottomRight) height = Math.Max(MinOverlayHeight, b.Height + dy);
        if (_zone is Zone.Top or Zone.TopLeft or Zone.TopRight) { height = Math.Max(MinOverlayHeight, b.Height - dy); top = b.Bottom - height; }
        Left = left;
        Top = top;
        Width = width;
        Height = height;
        if (_adjusting) OverlayChanged?.Invoke();   // 設定タブの数値の欄にすぐ出す
    }

    private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_pressed) return;
        _pressed = false;
        _root.ReleaseMouseCapture();
        if (_adjusting)
        {
            // 調整用: ドラッグを終えたら設定に入れてすぐ保存する。動かさないクリックは何もしない
            if (_dragged) { StoreBounds(); OverlayChanged?.Invoke(); }
            return;
        }
        if (_editing && !_dragged) Confirm();   // 動かさないクリック: 確定
    }

    // 中身を入れる枠: 中身を幅いっぱい (下限は MinChildWidth) で組み、高さが収まればそのまま出す。収まらないときだけ、全部が入る倍率まで全体を縮める
    // (縮めた分だけ広い幅で組み直すので、折り返しが減る。倍率は 2 分して探す)。大きくはしない
    private sealed class FitBox : Decorator
    {
        public double MinChildWidth;
        private readonly ScaleTransform _transform = new();
        private double _scale = 1;

        protected override Size MeasureOverride(Size constraint)
        {
            if (Child is not { } child) return new Size();
            if (double.IsInfinity(constraint.Width) || double.IsInfinity(constraint.Height) || constraint.Width <= 0 || constraint.Height <= 0)
            {
                _scale = 1;
                child.Measure(constraint);
                return child.DesiredSize;
            }
            // 倍率 s のときの、縮めた後の高さ (幅 constraint.Width / s で組む)
            double HeightAt(double s)
            {
                child.Measure(new Size(constraint.Width / s, double.PositiveInfinity));
                return child.DesiredSize.Height * s;
            }
            var hi = MinChildWidth > 0 ? Math.Min(1, constraint.Width / MinChildWidth) : 1;
            if (HeightAt(hi) <= constraint.Height) _scale = hi;
            else
            {
                var lo = 0.02;
                for (int i = 0; i < 14; i++)
                {
                    var mid = (lo + hi) / 2;
                    if (HeightAt(mid) <= constraint.Height) lo = mid; else hi = mid;
                }
                _scale = lo;
                HeightAt(lo);
            }
            return new Size(constraint.Width, Math.Min(constraint.Height, child.DesiredSize.Height * _scale));
        }

        protected override Size ArrangeOverride(Size arrangeSize)
        {
            if (Child is { } child)
            {
                _transform.ScaleX = _scale;
                _transform.ScaleY = _scale;
                if (!ReferenceEquals(child.RenderTransform, _transform)) child.RenderTransform = _transform;
                child.Arrange(new Rect(0, 0, arrangeSize.Width / _scale, child.DesiredSize.Height));
            }
            return arrangeSize;
        }
    }
}
