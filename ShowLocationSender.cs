using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StarCitizenJapaneseTextCreater;

// ゲーム (Star Citizen) のチャットに /showlocation を打ち込む (ゲームが座標をクリップボードに入れる)。［マップ］の［現在位置］から使う。
// 親が実機で確かめた PowerShell (showloc.ps1) を移したもの:
//   ゲームの窓 (Process 名 StarCitizen の MainWindowHandle) を前に出す (Alt を押して離してから SetForegroundWindow)、600ms 待つ、
//   SendInput の KEYEVENTF_SCANCODE で Enter、400ms 待つ、"/showlocation" の各キー、200ms 待つ、Enter。押して 10ms で離し、キーの間は 10ms
internal static class ShowLocationSender
{
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    // 64 ビットの INPUT (type の後に 8 バイトの境目で共用体。大きさ 40)
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    private const uint InputKeyboard = 1, KeyEventScanCode = 0x0008, KeyEventKeyUp = 0x0002;
    private const ushort ScanEnter = 0x1C;
    // "/showlocation" のスキャンコード (US / JIS とも / は 0x35)
    private static readonly ushort[] ShowLocationScans = { 0x35, 0x1F, 0x23, 0x18, 0x11, 0x26, 0x18, 0x2E, 0x1E, 0x14, 0x17, 0x18, 0x31 };

    // ゲームの窓 (無ければ IntPtr.Zero)
    public static IntPtr FindGameWindow()
    {
        foreach (var p in Process.GetProcessesByName("StarCitizen"))
        {
            try { if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle; }
            catch (InvalidOperationException) { }
            finally { p.Dispose(); }
        }
        return IntPtr.Zero;
    }

    // ゲームの窓が今いちばん前か
    public static bool IsForeground(IntPtr hwnd) => GetForegroundWindow() == hwnd;

    // Alt を押して離してから SetForegroundWindow (前に出しやすくする)。前に出せたら true
    public static bool BringToFront(IntPtr hwnd)
    {
        keybd_event(0x12, 0, 0, UIntPtr.Zero);
        keybd_event(0x12, 0, 2, UIntPtr.Zero);
        SetForegroundWindow(hwnd);
        return GetForegroundWindow() == hwnd;
    }

    // 送った結果: Sent = 全部送った、NotFront = ゲームの窓が前に出ず何も送っていない、Interrupted = 途中でゲームの窓が前から外れたので止めた (打ちかけの文字が残りうる)
    public enum SendResult { Sent, NotFront, Interrupted }

    // ゲームの窓を前に出して /showlocation を打ち込む (裏のスレッドで呼ぶ。数秒かかる)。
    // キーを 1 つ送る前ごとにゲームの窓が前にあるか確かめ、前に無ければそこで止める (ほかの窓に打ち込まないため)。
    // 最初のキーの前 (600ms 待った後) に前に無ければ NotFront、途中なら Interrupted
    // bringToFront が偽 (自動の取得) なら、ゲームの窓を前に出さない (前に無ければ、最初のキーの前で NotFront になる)
    public static SendResult Send(IntPtr gameWindow, bool bringToFront = true)
    {
        if (bringToFront)
        {
            BringToFront(gameWindow);
            Thread.Sleep(600);
        }
        if (!Scan(gameWindow, ScanEnter)) return SendResult.NotFront;
        Thread.Sleep(400);
        foreach (var sc in ShowLocationScans)
            if (!Scan(gameWindow, sc)) return SendResult.Interrupted;
        Thread.Sleep(200);
        if (!Scan(gameWindow, ScanEnter)) return SendResult.Interrupted;
        return SendResult.Sent;
    }

    // キー 1 つ (押して 10ms で離し、10ms 待つ。利用者の指示で 30ms から短くした)。送る前にゲームの窓が前に無ければ送らずに false
    private static bool Scan(IntPtr gameWindow, ushort sc)
    {
        if (GetForegroundWindow() != gameWindow) return false;
        Key(sc, false);
        Thread.Sleep(10);
        Key(sc, true);
        Thread.Sleep(10);
        return true;
    }

    private static void Key(ushort sc, bool up)
    {
        var i = new INPUT[1];
        i[0].type = InputKeyboard;
        i[0].ki.wScan = sc;
        i[0].ki.dwFlags = KeyEventScanCode | (up ? KeyEventKeyUp : 0u);
        SendInput(1, i, Marshal.SizeOf<INPUT>());
    }
}
