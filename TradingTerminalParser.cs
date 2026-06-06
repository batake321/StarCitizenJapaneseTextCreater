using System.Text.RegularExpressions;

namespace StarCitizenJapaneseTextCreater;

public class TradingTerminalParser
{
    private readonly Dictionary<string, int> _terminalNameToId;
    private readonly CommodityDictionary _dictionary;

    public TradingTerminalParser(
        Dictionary<string, int> terminalNameToId,
        CommodityDictionary dictionary)
    {
        _terminalNameToId = terminalNameToId;
        _dictionary = dictionary;
    }

    public TerminalCaptureData? Parse(OcrResult ocr)
    {
        if (ocr.Lines.Count == 0) return null;

        var result = new TerminalCaptureData { CapturedAt = DateTime.Now };

        DetectMode(ocr, result);
        ParseWithSpatialMatching(ocr, result);

        return result.Commodities.Count > 0 ? result : null;
    }

    private static void DetectMode(OcrResult ocr, TerminalCaptureData result)
    {
        foreach (var line in ocr.Lines.Take(10))
        {
            var text = line.Text.Replace(" ", "");
            if (text.Contains("売却") && !text.Contains("不可"))
            { result.Mode = "SELL"; return; }
            if (text.Contains("購入"))
            { result.Mode = "BUY"; return; }
        }
        result.Mode = "BUY";
    }

    // SCU pattern on space-stripped text: "12,000SCU", "6000SCIJ", "2100SCU"
    private static readonly Regex ScuRx = new(@"(\d[\d,]{0,8})\s*SC[UIJij]", RegexOptions.IgnoreCase);
    // Price/SCU pattern: "0.172/SCU", "293/SCU", "585/S(U"
    private static readonly Regex PriceRx = new(@"(\d[\d,.]{0,10})/S", RegexOptions.IgnoreCase);

    private static string StripSpaces(string s) => s.Replace(" ", "").Replace("　", "");

    private void ParseWithSpatialMatching(OcrResult ocr, TerminalCaptureData result)
    {
        for (int i = 0; i < ocr.Lines.Count; i++)
        {
            var line = ocr.Lines[i];
            var text = line.Text.Trim();
            if (string.IsNullOrEmpty(text) || text.Length < 2) continue;

            var match = _dictionary.FindBestMatch(text);
            if (match == null) continue;

            var (commodityId, displayName, confidence) = match.Value;
            if (result.Commodities.Any(c => c.CommodityId == commodityId)) continue;

            int inventory = 0;
            double price = 0;

            // Search this line and next several lines (space-stripped) for SCU and price
            for (int j = i; j < Math.Min(i + 6, ocr.Lines.Count); j++)
            {
                var stripped = StripSpaces(ocr.Lines[j].Text);

                if (inventory == 0)
                {
                    var scuMatch = ScuRx.Match(stripped);
                    if (scuMatch.Success)
                        int.TryParse(scuMatch.Groups[1].Value.Replace(",", ""), out inventory);
                }

                if (price == 0)
                {
                    var priceMatch = PriceRx.Match(stripped);
                    if (priceMatch.Success)
                        double.TryParse(priceMatch.Groups[1].Value.Replace(",", ""), out price);
                }

                if (inventory > 0 && price > 0) break;
            }

            result.Commodities.Add(new CapturedCommodityRow
            {
                RawName = text,
                MatchedName = displayName,
                CommodityId = commodityId,
                Price = price,
                Inventory = inventory,
                IsMatched = true,
                MatchConfidence = confidence,
            });
        }
    }
}

public class TerminalCaptureData
{
    public string TerminalName { get; set; } = "";
    public int TerminalId { get; set; }
    public string Mode { get; set; } = "BUY";
    public List<CapturedCommodityRow> Commodities { get; set; } = new();
    public byte[]? ScreenshotPng { get; set; }
    public DateTime CapturedAt { get; set; } = DateTime.Now;
}

public class CapturedCommodityRow : System.ComponentModel.INotifyPropertyChanged
{
    private string _rawName = "";
    private string _matchedName = "";
    private int _commodityId;
    private double _price;
    private int _inventory;
    private bool _isMatched;
    private double _matchConfidence;

    public string RawName
    {
        get => _rawName;
        set { _rawName = value; OnPropertyChanged(nameof(RawName)); }
    }
    public string MatchedName
    {
        get => _matchedName;
        set { _matchedName = value; OnPropertyChanged(nameof(MatchedName)); }
    }
    public int CommodityId
    {
        get => _commodityId;
        set { _commodityId = value; OnPropertyChanged(nameof(CommodityId)); }
    }
    public double Price
    {
        get => _price;
        set { _price = value; OnPropertyChanged(nameof(Price)); }
    }
    public int Inventory
    {
        get => _inventory;
        set { _inventory = value; OnPropertyChanged(nameof(Inventory)); }
    }
    public bool IsMatched
    {
        get => _isMatched;
        set { _isMatched = value; OnPropertyChanged(nameof(IsMatched)); }
    }
    public double MatchConfidence
    {
        get => _matchConfidence;
        set { _matchConfidence = value; OnPropertyChanged(nameof(MatchConfidence)); }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}
