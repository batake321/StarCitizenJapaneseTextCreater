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
//   中身: 窓の大きさに合わせて拡大縮小し、全部が見える (Viewbox。Stretch = Uniform)。
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
    // 位置を決めたことが無い間の大きさ (Config.cs の既定) と、変更中の大きさの下限・主画面の端からの間 (WPF の単位)
    public const double DefaultWidth = 760, DefaultHeight = 520;
    private const double MinOverlayWidth = 200, MinOverlayHeight = 120, ScreenMargin = 16;
    // 中身の元の幅 (この幅で組んでから、窓に合わせて拡大縮小する)
    private const double ContentWidth = 1100;
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

    // 中身を出す (窓が無ければ作る。あれば中身を入れ替え、消えるまでの時間を数え直す)。saveConfig: 確定したときに設定を保存する (MainWindow.SaveConfigToFile)
    public static void ShowContent(OreHere.Content content, Action? saveConfig)
    {
        var w = s_window;
        if (w == null)
        {
            w = new OreOverlayWindow(saveConfig);
            s_window = w;
            w.Closed += (_, _) => { if (ReferenceEquals(s_window, w)) s_window = null; };
        }
        w._saveConfig = saveConfig;
        w.SetContent(content);
        if (!w.IsVisible)
        {
            w.ApplyBounds();
            w.Show();
        }
        w.RestartDismissTimer();
    }

    // 開いていれば閉じる (アプリの終了時・設定でオフにしたとき)
    public static void CloseIfOpen()
    {
        try { s_window?.Close(); } catch { }
    }

    private Action? _saveConfig;
    private readonly Grid _root = new();
    private readonly StackPanel _content = new() { Width = ContentWidth };
    private readonly Border _editFrame;
    private readonly Grid _handles = new() { Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Border _closeBox;
    private readonly DispatcherTimer _modifierTimer = new() { Interval = TimeSpan.FromMilliseconds(ModifierPollMs) };
    private readonly DispatcherTimer _dismissTimer = new();
    private IntPtr _hwnd;
    private bool _clickThrough;
    // 変更中か、変更中のドラッグ (押している・動かした・どこを掴んだ・押した所 (画面)・押したときの窓)
    private bool _editing;
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

        // 背景 (半透明の暗い色) と中身 (窓の大きさに合わせて拡大縮小)
        _root.Children.Add(new Border
        {
            Background = PanelBrush, CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 10, 12, 10),
            Child = new Viewbox { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Child = _content },
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
            if (!_editing) Close();   // 変更中は時間で消さない (確定したときに数え直す)
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

    // クリックを通すか決め直す: 変更中は通さない。そうでなければ、左 Ctrl + 左 Shift を押している間だけ受け、ほかは通す
    private void UpdateClickThrough()
    {
        if (_hwnd == IntPtr.Zero) return;
        var through = !_editing && !LeftCtrlShiftDown();
        if (through == _clickThrough) return;
        var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, through ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT);
        _clickThrough = through;
    }

    // 出すときの位置と大きさ: 設定の値。位置を決めたことが無ければ主画面の作業領域の右上。画面 (全部の画面を合わせた範囲) の外になるときは中へ戻す
    private void ApplyBounds()
    {
        var c = App.Config;
        double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop, vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
        var w = double.IsFinite(c.OreOverlayWidth) && c.OreOverlayWidth >= MinOverlayWidth ? c.OreOverlayWidth : DefaultWidth;
        var h = double.IsFinite(c.OreOverlayHeight) && c.OreOverlayHeight >= MinOverlayHeight ? c.OreOverlayHeight : DefaultHeight;
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
        Width = w;
        Height = h;
        Left = left;
        Top = top;
    }

    // 消えるまでの時間を数え直す (0 なら出したまま。変更中は数えない)
    private void RestartDismissTimer()
    {
        _dismissTimer.Stop();
        var seconds = App.Config.OreOverlaySeconds;
        if (seconds <= 0 || _editing) return;
        _dismissTimer.Interval = TimeSpan.FromSeconds(seconds);
        _dismissTimer.Start();
    }

    // ===== 中身 =====

    private TextBlock Text(string text, Brush? brush = null, double size = 14, bool bold = false, Thickness? margin = null) => new()
    {
        Text = text, Foreground = brush ?? TextBrush, FontSize = size, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0),
    };

    private void SetContent(OreHere.Content c)
    {
        _content.Children.Clear();
        _content.Children.Add(Text(c.Title, size: 20, bold: true));
        if (c.Message.Length > 0) _content.Children.Add(Text(c.Message, margin: new Thickness(0, 6, 0, 0)));
        if (c.Sections.Count > 0)
        {
            // 表: 岩 | 格 | 出やすさ | シグネチャー | 岩の中の資源と割合 (最後の列は折り返す)
            var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            for (int i = 0; i < OreHere.Columns.Length; i++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = i == OreHere.Columns.Length - 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            int row = 0;
            void Cell(string text, int col, Brush? brush = null, bool bold = false, bool right = false, int span = 1, double top = 2)
            {
                var t = Text(text, brush, bold: bold, margin: new Thickness(0, top, col + span < OreHere.Columns.Length ? 14 : 0, 0));
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
            _content.Children.Add(Text(OreHere.QualityHeader, AccentBrush, bold: true, margin: new Thickness(0, 10, 0, 0)));
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < c.Quality.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var range = Text(c.Quality[i].Range, bold: true, margin: new Thickness(0, 2, 14, 0));
                var names = Text(c.Quality[i].Resources, margin: new Thickness(0, 2, 0, 0));
                Grid.SetRow(range, i);
                Grid.SetRow(names, i);
                Grid.SetColumn(names, 1);
                grid.Children.Add(range);
                grid.Children.Add(names);
            }
            _content.Children.Add(grid);
        }
        foreach (var n in c.QualityNotes) _content.Children.Add(Text(n, DimBrush, size: 12, margin: new Thickness(0, 4, 0, 0)));
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
        _pressed = false;
        _editFrame.Visibility = Visibility.Collapsed;
        _handles.Visibility = Visibility.Collapsed;
        _closeBox.Visibility = Visibility.Collapsed;
        _root.Cursor = null;
        UpdateClickThrough();
    }

    // 確定: 今の位置と大きさを設定に入れてすぐ保存し、クリックを通す状態に戻し、消えるまでの時間を数え直す
    private void Confirm()
    {
        if (double.IsFinite(Left) && double.IsFinite(Top) && double.IsFinite(Width) && double.IsFinite(Height))
        {
            var c = App.Config;
            c.OreOverlayLeft = Left;
            c.OreOverlayTop = Top;
            c.OreOverlayWidth = Width;
            c.OreOverlayHeight = Height;
            c.OreOverlayPlaced = true;
            try { _saveConfig?.Invoke(); }
            catch (Exception ex) { App.AppendStartupLog(App.Config.WorkingDirectory, $"[オーバーレイ] 位置と大きさを保存できませんでした: {ex.Message}"); }
        }
        ExitEdit();
        RestartDismissTimer();
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
    }

    private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_pressed) return;
        _pressed = false;
        _root.ReleaseMouseCapture();
        if (_editing && !_dragged) Confirm();   // 動かさないクリック: 確定
    }
}
