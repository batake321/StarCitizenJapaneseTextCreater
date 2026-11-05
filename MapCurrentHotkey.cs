using System.Runtime.InteropServices;
using System.Windows.Input;

namespace StarCitizenJapaneseTextCreater;

// ［マップ］の［現在位置］をキーで動かすためのキー (2026-11-05、利用者の指示「現在位置ボタンはゲームがアクティブでも L-CTRL + L-Shift + Ins が
// ゲームとかぶらなければデフォルト(設定可能)で動作するようにするとかなり便利」)。
//   受け方: キーボードの Raw Input (2026-11-05 に RegisterHotKey・WM_HOTKEY から置き換えた。利用者の報告「というか、効かない 押しても L-Shift + L-Alt + Ins にしても
//     会話に /showlocation 送られない」。RegisterHotKey の登録は成功していたが、ゲームが前にある間に押しても WM_HOTKEY の先の処理が 1 回も動いていなかった)。
//     RegisterRawInputDevices (usage page 1・usage 6 = キーボード、RIDEV_INPUTSINK、hwndTarget = 主の窓) で登録し、WM_INPUT を主の窓のフック (HwndSource.AddHook) で受ける。
//     RIDEV_INPUTSINK の説明「this enables the caller to receive the input even when the caller is not in the foreground」。
//     キーは取り上げない (RIDEV_NOLEGACY を付けない)。押したキーはゲームにもほかのアプリにもそのまま届く。
//     WM_INPUT は handled を立てず、今までどおり DefWindowProc へ流す (WM_INPUT の説明「RIM_INPUT … The application must call DefWindowProc so the system can perform cleanup.」)。
//     主のキーが押された (make) ときに 1 回だけ判定する。押しっぱなしの繰り返しは、離される (break) まで無視する。
//   修飾キーの判定 (Mismatch): 主のキーが押されたときに GetAsyncKeyState で左右別の仮想キー (VK_LCONTROL 0xA2 / VK_RCONTROL 0xA3 / VK_LSHIFT 0xA0 / VK_RSHIFT 0xA1 /
//     VK_LMENU 0xA4 / VK_RMENU 0xA5) の最上位ビット (今押されている) を見る。RegisterHotKey のときと同じ決まり: Ctrl・Shift・Alt のそれぞれが「決めたキーにある ⇔ 左右どちらかが
//     押されている」で一致し (決めていない修飾キーが押されていたら動かさない。Windows キーが押されていても動かさない)、そのうえで決めた側のキーが押されているときだけ動かす
//     (右 Alt・右 Shift で押したときは何もしない)。
//   主のキーは仮想キーで見る。設定の書き方 (System.Windows.Input.Key の名前) では矢印の上の Insert とテンキーの 0 の Insert (NumLock が切のとき) を書き分けられないので、どちらでも動く。
//   既定は 右 Shift + 左 Alt + Insert (DefaultText)。v2.6.6・v2.6.7 の既定は 左 Alt + 左 Shift + Insert (PreviousDefaultText2)、v2.6.4・v2.6.5 の既定は 左 Ctrl + 左 Shift + Insert だった (PreviousDefaultText)。
//     (2026-11-06、利用者の指示「現在位置の初期値は R-Shift + L-Alt + Ins がいいかも L-Shift 割り当てある」「2 変える」。ゲームでは左 Shift にも割り当てがある)
//     (利用者の指示「CTRL しゃがみなんで、L-Alt + L-Shift + Ins じゃないとまずい」。ゲームでは Ctrl がしゃがみなので、左 Ctrl を含むキーだと押したときにしゃがむ)
//     2026-11-05 に、ゲームの既定の割り当て (Data.p4k の Data/Libs/Config/defaultProfile.xml) に Insert を使う行が 1 つも無いことを確かめた
internal sealed class MapCurrentHotkey
{
    // キー 1 つ: 修飾キー (左右別) と主のキー
    public sealed record Spec(bool LCtrl, bool RCtrl, bool LShift, bool RShift, bool LAlt, bool RAlt, Key Key)
    {
        public bool HasModifier => LCtrl || RCtrl || LShift || RShift || LAlt || RAlt;
    }

    // 設定の文字 (App.Config.MapCurrentHotkey) の既定: 右 Shift + 左 Alt + Insert。前の版の既定: v2.6.4・v2.6.5 は 左 Ctrl + 左 Shift + Insert、v2.6.6・v2.6.7 は 左 Alt + 左 Shift + Insert
    public const string DefaultText = "RShift+LAlt+Insert";
    public const string PreviousDefaultText = "LCtrl+LShift+Insert";
    public const string PreviousDefaultText2 = "LAlt+LShift+Insert";

    // 設定を読んだとき (AppConfigLoader) に 1 回だけ: キーが前の版の既定のままなら、今の既定 (右 Shift + 左 Alt + Insert) に替える。
    //   v2.6.6・v2.6.7 の既定 (左 Alt + 左 Shift + Insert) のまま → 替える。
    //   v2.6.4・v2.6.5 の既定 (左 Ctrl + 左 Shift + Insert) のまま → 前の替え (印 MapCurrentHotkeyDefaultMigrated) がまだのときだけ替える
    //   (前の替えが済んだ後に自分で 左 Ctrl + 左 Shift + Insert を選び直した人のキーは替えない)。
    // 前の版は設定の保存で全部の項目を書くので、1 回でも保存した人の設定には前の既定がもう書かれていて、既定を変えただけでは直らないため。
    // 替えたかどうかにかかわらず、済んだ印 (AppConfig.MapCurrentHotkeyDefaultMigrated・MapCurrentHotkeyDefaultMigrated2) を立てる。印は次に設定が保存されたときにファイルに入り、
    // その後は利用者が自分で前の既定を選び直しても替えない (印が保存されるまでは、起動のたびに同じ判定をする)。
    // 使う・使わない (MapCurrentHotkeyEnabled) は変えない
    public static void MigrateDefault(AppConfig config)
    {
        if (config.MapCurrentHotkeyDefaultMigrated2) return;
        if (Parse(config.MapCurrentHotkey) is { } now && (now == Parse(PreviousDefaultText2) || (!config.MapCurrentHotkeyDefaultMigrated && now == Parse(PreviousDefaultText))))
            config.MapCurrentHotkey = DefaultText;
        config.MapCurrentHotkeyDefaultMigrated = true;
        config.MapCurrentHotkeyDefaultMigrated2 = true;
    }

    private static readonly (string Token, string Label)[] ModifierNames =
        [("LCtrl", "左 Ctrl"), ("RCtrl", "右 Ctrl"), ("LShift", "左 Shift"), ("RShift", "右 Shift"), ("LAlt", "左 Alt"), ("RAlt", "右 Alt")];

    private static bool[] Flags(Spec s) => [s.LCtrl, s.RCtrl, s.LShift, s.RShift, s.LAlt, s.RAlt];

    // 修飾キーそのもの (主のキーにできない)
    public static bool IsModifierKey(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    // 修飾キー無しで使えるキー (字を打たないキーだけ)
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

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices([In] RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, [Out] byte[] pData, ref uint pcbSize, uint cbSizeHeader);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    private const int WM_INPUT = 0x00FF;
    // キーボード (HID の usage page 1 = Generic Desktop、usage 6 = Keyboard)
    private const ushort UsagePageGeneric = 0x01, UsageKeyboard = 0x06;
    private const uint RIDEV_REMOVE = 0x00000001, RIDEV_INPUTSINK = 0x00000100;
    private const uint RID_INPUT = 0x10000003, RIM_TYPEKEYBOARD = 1;
    // RAWKEYBOARD.Flags: 1 = キーが離された (RI_KEY_BREAK)。0 = 押された (RI_KEY_MAKE)
    private const ushort RI_KEY_BREAK = 1;
    private const int VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    // RAWINPUTHEADER の大きさ (dwType 4 + dwSize 4 + hDevice + wParam)。RAWKEYBOARD はその直後 (MakeCode 2・Flags 2・Reserved 2・VKey 2・Message 4・ExtraInformation 4)
    private static readonly int HeaderSize = 8 + 2 * IntPtr.Size;
    // 修飾キーが合わずに何もしなかった知らせ (Ignored) の間隔
    private const long IgnoredIntervalMs = 10000;

    private Spec? _registered;
    // 主のキーが押されたままか (押しっぱなしの繰り返しを無視するため。離されたら偽)
    private bool _mainDown;
    private long _lastIgnoredTick = -IgnoredIntervalMs;
    private readonly byte[] _raw = new byte[64];

    // キーが押された (修飾キーが決めたとおりに押されていたときだけ)
    public event Action? Pressed;

    // 主のキーが押されたが、修飾キーが合わないので何もしなかった (引数は、そのとき押されていた修飾キーの文。続けて出ないように IgnoredIntervalMs に 1 回まで)
    public event Action<string>? Ignored;

    // 今登録しているキー (登録していなければ null)
    public Spec? Registered => _registered;

    // 仮想キーが今押されているか (GetAsyncKeyState の戻り値の最上位ビット)
    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static RAWINPUTDEVICE[] Device(uint flags, IntPtr hwnd) =>
        [new RAWINPUTDEVICE { usUsagePage = UsagePageGeneric, usUsage = UsageKeyboard, dwFlags = flags, hwndTarget = hwnd }];

    // 主の窓に、キーボードの Raw Input を登録する (前の登録は外す。アプリの窓が前に無くても届く形)。登録できなければ false (lastError に Win32 のエラー番号)
    public bool Register(IntPtr hwnd, Spec spec, out int lastError)
    {
        Unregister();
        lastError = 0;
        if (!RegisterRawInputDevices(Device(RIDEV_INPUTSINK, hwnd), 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            lastError = Marshal.GetLastWin32Error();
            return false;
        }
        _registered = spec;
        return true;
    }

    // Raw Input の登録を外す (RIDEV_REMOVE のときは hwndTarget を NULL にする決まり)
    public void Unregister()
    {
        if (_registered != null) RegisterRawInputDevices(Device(RIDEV_REMOVE, IntPtr.Zero), 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        _registered = null;
        _mainDown = false;
    }

    // 主の窓のフック: WM_INPUT (キーボード) を受ける。主のキーが押された (make) ときに、修飾キーが決めたとおりなら Pressed を起こす。
    // handled は立てない (後ろの DefWindowProc へ流す)
    public IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_INPUT || _registered is not { } s) return IntPtr.Zero;
        var size = (uint)_raw.Length;
        var got = GetRawInputData(lParam, RID_INPUT, _raw, ref size, (uint)HeaderSize);
        if (got == uint.MaxValue || got < HeaderSize + 8 || BitConverter.ToUInt32(_raw, 0) != RIM_TYPEKEYBOARD) return IntPtr.Zero;
        var flags = BitConverter.ToUInt16(_raw, HeaderSize + 2);
        var vkey = BitConverter.ToUInt16(_raw, HeaderSize + 6);
        if (vkey != KeyInterop.VirtualKeyFromKey(s.Key)) return IntPtr.Zero;
        if ((flags & RI_KEY_BREAK) != 0) { _mainDown = false; return IntPtr.Zero; }
        if (_mainDown) return IntPtr.Zero;   // 押しっぱなしの繰り返し
        _mainDown = true;
        if (Mismatch(s, Down) is not { } why) Pressed?.Invoke();
        else if (Environment.TickCount64 - _lastIgnoredTick >= IgnoredIntervalMs)
        {
            _lastIgnoredTick = Environment.TickCount64;
            Ignored?.Invoke(why);
        }
        return IntPtr.Zero;
    }

    // 修飾キーが決めたとおりに押されていれば null、合わなければ、そのとき押されていた修飾キーの文。down: 仮想キーが今押されているか。
    //   Ctrl・Shift・Alt のそれぞれ: 決めたキーにある ⇔ 左右どちらかが押されている (RegisterHotKey の MOD_CONTROL / MOD_SHIFT / MOD_ALT の一致と同じ)。Windows キーは押されていない。
    //   そのうえで、決めた側のキーが全部押されている (例: 左 Alt + 左 Shift なら VK_LMENU と VK_LSHIFT)
    internal static string? Mismatch(Spec s, Func<int, bool> down)
    {
        bool lc = down(VK_LCONTROL), rc = down(VK_RCONTROL), ls = down(VK_LSHIFT), rs = down(VK_RSHIFT), la = down(VK_LMENU), ra = down(VK_RMENU);
        bool lw = down(VK_LWIN), rw = down(VK_RWIN);
        var ok = (lc || rc) == (s.LCtrl || s.RCtrl) && (ls || rs) == (s.LShift || s.RShift) && (la || ra) == (s.LAlt || s.RAlt) && !lw && !rw
            && (!s.LCtrl || lc) && (!s.RCtrl || rc) && (!s.LShift || ls) && (!s.RShift || rs) && (!s.LAlt || la) && (!s.RAlt || ra);
        if (ok) return null;
        bool[] held = [lc, rc, ls, rs, la, ra];
        var names = ModifierNames.Where((_, i) => held[i]).Select(m => m.Label).ToList();
        if (lw) names.Add("左 Windows");
        if (rw) names.Add("右 Windows");
        return names.Count == 0 ? "修飾キーは押されていませんでした" : "押されていた修飾キー: " + string.Join(" + ", names);
    }

    // 修飾キー (Ctrl・Shift・Alt・Windows の左右とも) か、登録しているキーの主のキーが、今押されているか
    // (キーが離されるのを待ってからゲームにキーを送るため。MapPanel.CaptureCurrentFromHotkeyAsync)
    public bool AnyKeyDown()
    {
        if (Down(VK_LCONTROL) || Down(VK_RCONTROL) || Down(VK_LSHIFT) || Down(VK_RSHIFT) || Down(VK_LMENU) || Down(VK_RMENU) || Down(VK_LWIN) || Down(VK_RWIN)) return true;
        return _registered is { } s && Down(KeyInterop.VirtualKeyFromKey(s.Key));
    }
}
