namespace StarCitizenJapaneseTextCreater;

public interface IOcrEngine
{
    string Name { get; }
    Task<OcrResult> RecognizeAsync(byte[] pngImage);
}

public class OcrResult
{
    public string FullText { get; set; } = "";
    public List<OcrLine> Lines { get; set; } = new();
    public double Confidence { get; set; }
    public TimeSpan ProcessingTime { get; set; }
}

public class OcrLine
{
    public string Text { get; set; } = "";
    public double Confidence { get; set; }
    public int Y { get; set; }
    public List<OcrWord> Words { get; set; } = new();
}

public class OcrWord
{
    public string Text { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}
