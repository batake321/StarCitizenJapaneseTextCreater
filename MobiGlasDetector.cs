using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StarCitizenJapaneseTextCreater;

// ［マップ］の［現在位置］(ボタンと自動) でゲームにキーを送る前に、mobiGlas が開いているかを画面で確かめる。
//   mobiGlas が開いているときは、画面のいちばん下に所持金と切り替えのタブの帯 (ホーム・ヘルス・通信・契約・マップ・…) が出る (親が実際の画面で確かめた)。
//   ゲームの窓の矩形の下の部分 (高さの下から 12%、横は全部) を撮り (ファイルには保存しない)、Windows の OCR (日本語。足りなければ英語も) で読んで、
//   タブの言葉が 2 つ以上あれば開いているとする
internal static class MobiGlasDetector
{
    // mobiGlas の切り替えのタブの言葉 (日本語化した状態と英語)
    private static readonly string[] TabWords =
    {
        "ホーム", "ヘルス", "通信", "契約", "マップ", "ジャーナル", "アセット", "評判", "ウォレット", "ランディング", "ビークル",
        "Home", "Health", "Comms", "Contracts", "Map", "Journal", "Assets", "Reputation", "Wallet", "Landing", "Vehicles",
    };
    // 開いているとみなす言葉の数
    private const int OpenThreshold = 2;
    // 撮る高さ (窓の高さに対する割合。下から)
    private const double BandRatio = 0.12;

    // 判定の結果: Open = 開いているか、Hits = 読めたタブの言葉、Text = OCR の文字、Elapsed = 撮る + OCR の時間
    public sealed record Result(bool Open, IReadOnlyList<string> Hits, string Text, TimeSpan Elapsed);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

    // ゲームの窓 (gameWindow) の下の帯を撮って確かめる。撮れない・OCR が使えない (例外・言語が無い) ときは例外を投げる (呼ぶ側は確かめずに送る)
    public static async Task<Result> CheckWindowAsync(IntPtr gameWindow)
    {
        var sw = Stopwatch.StartNew();
        if (!GetClientRect(gameWindow, out var rc)) throw new InvalidOperationException("ゲームの窓の大きさを読めませんでした");
        var origin = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(gameWindow, ref origin)) throw new InvalidOperationException("ゲームの窓の位置を読めませんでした");
        int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
        int bandH = (int)Math.Round(h * BandRatio);
        var png = ScreenCaptureService.CaptureRectAsPng(origin.X, origin.Y + h - bandH, w, bandH)
                  ?? throw new InvalidOperationException("ゲームの窓を撮れませんでした");
        var r = await DetectAsync(png);
        sw.Stop();
        return r with { Elapsed = sw.Elapsed };
    }

    // 帯の画像 (PNG) を読んで判定する。日本語で読み、タブの言葉が 2 つ未満なら英語でも読んで合わせる。どちらの言語の OCR も無ければ例外
    public static async Task<Result> DetectAsync(byte[] bandPng)
    {
        var sw = Stopwatch.StartNew();
        var ja = await WindowsOcrEngine.RecognizeWholeTextAsync(bandPng, "ja");
        var hits = Hits(ja ?? "");
        var text = ja ?? "";
        if (hits.Count < OpenThreshold)
        {
            var en = await WindowsOcrEngine.RecognizeWholeTextAsync(bandPng, "en-US");
            if (ja == null && en == null) throw new InvalidOperationException("Windows OCR の日本語・英語が使えません");
            if (en != null)
            {
                text += (text.Length > 0 ? " / " : "") + en;
                foreach (var hw in Hits(en)) if (!hits.Contains(hw)) hits.Add(hw);
            }
        }
        sw.Stop();
        return new Result(hits.Count >= OpenThreshold, hits, text, sw.Elapsed);
    }

    // 文字の中のタブの言葉 (日本語は OCR が字の間に入れる空白を除いて比べる。英語は大文字小文字を区別しない)
    private static List<string> Hits(string text)
    {
        var compact = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        return TabWords.Where(wd => compact.Contains(wd, StringComparison.OrdinalIgnoreCase) || text.Contains(wd, StringComparison.OrdinalIgnoreCase)).ToList();
    }
}
