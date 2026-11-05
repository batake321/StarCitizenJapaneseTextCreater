using System.Runtime.InteropServices;
using System.Windows.Input;

namespace StarCitizenJapaneseTextCreater;

// ［マップ］の［現在位置］をキーで動かすためのキー (2026-11-05、利用者の指示「現在位置ボタンはゲームがアクティブでも L-CTRL + L-Shift + Ins が
// ゲームとかぶらなければデフォルト(設定可能)で動作するようにするとかなり便利」)。
//   登録: PrintScreen の価格キャプチャ (ScreenCaptureService) と同じく RegisterHotKey で主の窓に登録し、WM_HOTKEY を窓のフック (HwndSource.AddHook) で受ける。
//     アプリの窓が前に無くても (ゲームが前でも) 届く。キーを押し続けても 1 回だけ (MOD_NOREPEAT)。
//   左右の区別: RegisterHotKey の MOD_CONTROL / MOD_SHIFT / MOD_ALT は左右どちらのキーでも当たる (説明「Either CTRL key must be held down.」) ので、
//     WM_HOTKEY が来たときに GetAsyncKeyState で左右別の仮想キー (VK_LCONTROL 0xA2 / VK_RCONTROL 0xA3 / VK_LSHIFT 0xA0 / VK_RSHIFT 0xA1 / VK_LMENU 0xA4 / VK_RMENU 0xA5)
//     の最上位ビット (今押されている) を見て、決めた側のキーが押されているときだけ動かす (右 Ctrl・右 Shift で押したときは何もしない)。
//   既定は 左 Ctrl + 左 Shift + Insert (DefaultText)。2026-11-05 に、ゲームの既定の割り当て (Data.p4k の Data/Libs/Config/defaultProfile.xml) に Insert を使う
//     行が 1 つも無いことを確かめた
internal sealed class MapCurrentHotkey
{
    // キー 1 つ: 修飾キー (左右別) と主のキー
    public sealed record Spec(bool LCtrl, bool RCtrl, bool LShift, bool RShift, bool LAlt, bool RAlt, Key Key)
    {
        public bool HasModifier => LCtrl || RCtrl || LShift || RShift || LAlt || RAlt;
    }

    // 設定の文字 (App.Config.MapCurrentHotkey) の既定: 左 Ctrl + 左 Shift + Insert
    public const string DefaultText = "LCtrl+LShift+Insert";

    private static readonly (string Token, string Label)[] ModifierNames =
        [("LCtrl", "左 Ctrl"), ("RCtrl", "右 Ctrl"), ("LShift", "左 Shift"), ("RShift", "右 Shift"), ("LAlt", "左 Alt"), ("RAlt", "右 Alt")];

    private static bool[] Flags(Spec s) => [s.LCtrl, s.RCtrl, s.LShift, s.RShift, s.LAlt, s.RAlt];

    // 修飾キーそのもの (主のキーにできない)
    public static bool IsModifierKey(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    // 修飾キー無しで使えるキー (字を打つキーを修飾キー無しで登録すると、どのアプリでもその字が打てなくなるため、字を打たないキーだけ)
    private static bool AllowedAlone(Key k) => k is >= Key.F1 and <= Key.F24 or Key.Insert or Key.Delete or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Pause or Key.Scroll;

    // 設定の文字 → キー。読めなければ null ("+" で区切る。修飾キーの名前 (大文字小文字は区別しない) の後に主のキー (System.Windows.Input.Key の名前) が 1 つ)
    public static Spec? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var flags = new bool[ModifierNames.Length];
        Key? key = null;
        foreach (var raw in text.Split('+'))
        {
            var t = raw.Trim();
            var mi = Array.FindIndex(ModifierNames, m => m.Token.Equals(t, StringComparison.OrdinalIgnoreCase));
            if (mi >= 0) { flags[mi] = true; continue; }
            if (key != null || t.Length == 0 || char.IsDigit(t[0]) || !Enum.TryParse<Key>(t, ignoreCase: true, out var k) || !Enum.IsDefined(k)) return null;
            key = k;
        }
        if (key is not { } main) return null;
        var spec = new Spec(flags[0], flags[1], flags[2], flags[3], flags[4], flags[5], main);
        return Validate(spec) == null ? spec : null;
    }

    // キー → 設定の文字 (例 "LCtrl+LShift+Insert")
    public static string ToConfigText(Spec s)
    {
        var flags = Flags(s);
        var parts = ModifierNames.Where((_, i) => flags[i]).Select(m => m.Token).ToList();
        parts.Add(s.Key.ToString());
        return string.Join("+", parts);
    }

    // キー → 画面の文字 (例 「左 Ctrl + 左 Shift + Insert」)
    public static string Display(Spec s)
    {
        var flags = Flags(s);
        var parts = ModifierNames.Where((_, i) => flags[i]).Select(m => m.Label).ToList();
        parts.Add(KeyLabel(s.Key));
        return string.Join(" + ", parts);
    }

    // 主のキーの画面の文字 (同じ値に名前が 2 つあるキーと数字のキーは、分かりやすいほうにする。ほかは Key の名前のまま)
    private static string KeyLabel(Key k) => k switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(k - Key.D0)).ToString(),
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.Enter => "Enter",
        Key.CapsLock => "CapsLock",
        Key.PrintScreen => "PrintScreen",
        Key.Escape => "Esc",
        Key.Back => "BackSpace",
        _ => k.ToString(),
    };

    // 使えないキーなら理由、使えれば null
    public static string? Validate(Spec s)
    {
        if (s.Key == Key.None || IsModifierKey(s.Key)) return "主のキー (修飾キーでないキー) がありません";
        if (KeyInterop.VirtualKeyFromKey(s.Key) == 0) return "このキーは登録できません";
        if (!s.HasModifier && !AllowedAlone(s.Key)) return "このキーは Ctrl・Shift・Alt と一緒に押してください";
        return null;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    // ホットキーの番号 (PrintScreen の価格キャプチャは 0x9001)
    private const int HotkeyId = 0x9002;
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_NOREPEAT = 0x4000;
    private const int VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    private IntPtr _hwnd;
    private Spec? _registered;

    // キーが押された (決めた側の修飾キーで押されたときだけ)
    public event Action? Pressed;

    // 今登録しているキー (登録していなければ null)
    public Spec? Registered => _registered;

    // 仮想キーが今押されているか (GetAsyncKeyState の戻り値の最上位ビット)
    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    // 主の窓に登録する (前の登録は外す)。登録できなければ false (ほかのアプリが同じキーを使っているなど。lastError に Win32 のエラー番号)
    public bool Register(IntPtr hwnd, Spec spec, out int lastError)
    {
        Unregister();
        lastError = 0;
        uint mods = MOD_NOREPEAT;
        if (spec.LCtrl || spec.RCtrl) mods |= MOD_CONTROL;
        if (spec.LShift || spec.RShift) mods |= MOD_SHIFT;
        if (spec.LAlt || spec.RAlt) mods |= MOD_ALT;
        if (!RegisterHotKey(hwnd, HotkeyId, mods, (uint)KeyInterop.VirtualKeyFromKey(spec.Key)))
        {
            lastError = Marshal.GetLastWin32Error();
            return false;
        }
        _hwnd = hwnd;
        _registered = spec;
        return true;
    }

    public void Unregister()
    {
        if (_registered != null && _hwnd != IntPtr.Zero) UnregisterHotKey(_hwnd, HotkeyId);
        _registered = null;
    }

    // 主の窓のフック: WM_HOTKEY (このキーの番号) を受ける。決めた側の修飾キーが押されているときだけ Pressed を起こす
    public IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            if (_registered is { } s && SidesDown(s)) Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    // 決めた側の修飾キーが全部押されているか (左右別の仮想キーで見る。例: 左 Ctrl + 左 Shift なら VK_LCONTROL と VK_LSHIFT)
    private static bool SidesDown(Spec s) =>
        (!s.LCtrl || Down(VK_LCONTROL)) && (!s.RCtrl || Down(VK_RCONTROL))
        && (!s.LShift || Down(VK_LSHIFT)) && (!s.RShift || Down(VK_RSHIFT))
        && (!s.LAlt || Down(VK_LMENU)) && (!s.RAlt || Down(VK_RMENU));

    // 修飾キー (Ctrl・Shift・Alt・Windows の左右とも) か、登録しているキーの主のキーが、今押されているか
    // (キーが離されるのを待ってからゲームにキーを送るため。MapPanel.CaptureCurrentFromHotkeyAsync)
    public bool AnyKeyDown()
    {
        if (Down(VK_LCONTROL) || Down(VK_RCONTROL) || Down(VK_LSHIFT) || Down(VK_RSHIFT) || Down(VK_LMENU) || Down(VK_RMENU) || Down(VK_LWIN) || Down(VK_RWIN)) return true;
        return _registered is { } s && Down(KeyInterop.VirtualKeyFromKey(s.Key));
    }
}
