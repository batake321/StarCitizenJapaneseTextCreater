using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace StarCitizenJapaneseTextCreater;

public class ScreenCaptureService : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h,
        IntPtr hdcSrc, int x1, int y1, uint rop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int HOTKEY_ID = 0x9001;
    private const uint VK_SNAPSHOT = 0x2C;
    private const int WM_HOTKEY = 0x0312;
    private const uint SRCCOPY = 0x00CC0020;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    private IntPtr _hwnd;
    private bool _registered;

    public event Action<byte[]>? OnScreenCaptured;
    public event Action<string>? OnLog;

    public void Register(IntPtr hwnd)
    {
        _hwnd = hwnd;
        if (RegisterHotKey(hwnd, HOTKEY_ID, 0, VK_SNAPSHOT))
        {
            _registered = true;
            OnLog?.Invoke("PrintScreen ホットキー登録完了");
        }
        else
        {
            OnLog?.Invoke("PrintScreen ホットキー登録失敗 (他のアプリが使用中の可能性があります)");
        }
    }

    public void Unregister()
    {
        if (_registered && _hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, HOTKEY_ID);
            _registered = false;
        }
    }

    public IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            handled = true;
            var png = CaptureScreenAsPng();
            if (png != null)
                OnScreenCaptured?.Invoke(png);
        }
        return IntPtr.Zero;
    }

    public byte[]? CaptureScreenAsPng()
    {
        var w = GetSystemMetrics(SM_CXSCREEN);
        var h = GetSystemMetrics(SM_CYSCREEN);
        if (w <= 0 || h <= 0) return null;

        var desktopDC = GetDC(IntPtr.Zero);
        var memDC = CreateCompatibleDC(desktopDC);
        var hBitmap = CreateCompatibleBitmap(desktopDC, w, h);
        var oldBitmap = SelectObject(memDC, hBitmap);

        BitBlt(memDC, 0, 0, w, h, desktopDC, 0, 0, SRCCOPY);

        SelectObject(memDC, oldBitmap);

        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();

            using var ms = new MemoryStream();
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
            encoder.Save(ms);
            return ms.ToArray();
        }
        finally
        {
            DeleteObject(hBitmap);
            DeleteDC(memDC);
            ReleaseDC(IntPtr.Zero, desktopDC);
        }
    }

    public void Dispose()
    {
        Unregister();
    }
}
