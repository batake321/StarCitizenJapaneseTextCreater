using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using WinBitmapDecoder = Windows.Graphics.Imaging.BitmapDecoder;
using WpfBitmapFrame = System.Windows.Media.Imaging.BitmapFrame;

namespace StarCitizenJapaneseTextCreater;

public class WindowsOcrEngine : IOcrEngine
{
    public string Name => "Windows OCR";

    // Crop region for the right commodity panel (ratio-based for any resolution)
    private const double CropLeft = 0.547;   // ~1050/1920
    private const double CropTop = 0.111;    // ~120/1080
    private const double CropWidth = 0.391;  // ~750/1920
    private const double CropHeight = 0.787; // ~850/1080

    private const double GammaValue = 2.5;
    private const int ScaleUpWidth = 2000;

    public async Task<OcrResult> RecognizeAsync(byte[] pngImage)
    {
        var sw = Stopwatch.StartNew();

        // Try ja first, then en-US
        var engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("ja"))
                  ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
        if (engine == null)
            throw new InvalidOperationException(
                "Windows OCR の日本語または英語言語パックがインストールされていません。");

        var processed = PreprocessImage(pngImage);

        SoftwareBitmap softwareBitmap;
        using (var stream = new InMemoryRandomAccessStream())
        {
            await stream.WriteAsync(processed.AsBuffer());
            stream.Seek(0);
            var decoder = await WinBitmapDecoder.CreateAsync(stream);
            softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        }

        var ocrResult = await engine.RecognizeAsync(softwareBitmap);
        sw.Stop();

        var lines = new List<OcrLine>();
        foreach (var line in ocrResult.Lines)
        {
            var words = new List<OcrWord>();
            foreach (var word in line.Words)
            {
                words.Add(new OcrWord
                {
                    Text = word.Text,
                    X = (int)word.BoundingRect.X,
                    Y = (int)word.BoundingRect.Y,
                    Width = (int)word.BoundingRect.Width,
                    Height = (int)word.BoundingRect.Height,
                });
            }
            lines.Add(new OcrLine
            {
                Text = line.Text,
                Confidence = 1.0,
                Y = words.Count > 0 ? words[0].Y : 0,
                Words = words,
            });
        }

        return new OcrResult
        {
            FullText = ocrResult.Text,
            Lines = lines,
            Confidence = lines.Count > 0 ? 1.0 : 0.0,
            ProcessingTime = sw.Elapsed,
        };
    }

    private static byte[] PreprocessImage(byte[] pngImage)
    {
        // Load
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.StreamSource = new MemoryStream(pngImage);
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.EndInit();
        bi.Freeze();

        int imgW = bi.PixelWidth, imgH = bi.PixelHeight;

        // Crop right panel
        int cx = (int)(imgW * CropLeft);
        int cy = (int)(imgH * CropTop);
        int cw = (int)(imgW * CropWidth);
        int ch = (int)(imgH * CropHeight);
        cx = Math.Min(cx, imgW - 1);
        cy = Math.Min(cy, imgH - 1);
        cw = Math.Min(cw, imgW - cx);
        ch = Math.Min(ch, imgH - cy);

        var cropped = new CroppedBitmap(bi, new Int32Rect(cx, cy, cw, ch));
        cropped.Freeze();

        // Scale up
        double scale = Math.Max(1.0, (double)ScaleUpWidth / cw);
        var scaled = new TransformedBitmap(cropped, new ScaleTransform(scale, scale));
        scaled.Freeze();

        // Convert to BGRA32 pixels
        var bgra = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
        bgra.Freeze();

        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        int stride = w * 4;
        var pixels = new byte[h * stride];
        bgra.CopyPixels(pixels, stride, 0);

        // Gamma correction
        var lut = new byte[256];
        for (int i = 0; i < 256; i++)
            lut[i] = (byte)Math.Clamp(Math.Pow(i / 255.0, 1.0 / GammaValue) * 255.0, 0, 255);

        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i]     = lut[pixels[i]];     // B
            pixels[i + 1] = lut[pixels[i + 1]]; // G
            pixels[i + 2] = lut[pixels[i + 2]]; // R
        }

        // Encode to PNG
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();

        using var ms = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(WpfBitmapFrame.Create(result));
        encoder.Save(ms);
        return ms.ToArray();
    }
}
