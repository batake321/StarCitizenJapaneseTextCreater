namespace StarCitizenJapaneseTextCreater;

public class CommodityDictionary
{
    private readonly List<DictionaryEntry> _entries = new();

    // Known UI keywords to skip (not commodity names)
    private static readonly HashSet<string> SkipKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ショップインベントリ", "ショップインペントリ",
        "購入", "売却", "在庫あり", "在庫切れ", "在庫なし",
        "需要あり", "需要なし", "売却不可",
        "インベントリ", "インペントリ",
        "最大", "非常に高い", "高い", "中", "低い", "非常に低い",
        "コモディティ", "現在の残高",
        "有効なインベントリを選択して", "有効なインペントリを選択して",
        "取引を行ってください",
        "ロケーションを選択", "ロケーションを退択",
        "AUEC", "SCU", "FPS", "GPU", "CPU",
        "SHOP", "INVENTORY", "BUY", "SELL",
    };

    // Game display name alternatives not always in translations.db
    private static readonly Dictionary<string, string[]> KnownAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Scrap"] = new[] { "スクラップ", "廃棄" },
        ["Agricultural Supplies"] = new[] { "農業用品", "農業資材" },
        ["Hydrogen Fuel"] = new[] { "水素燃料", "プラズマ燃料" },
        ["Quantum Fuel"] = new[] { "量子燃料", "クォンタム燃料" },
        ["Titanium"] = new[] { "チタニウム", "チタン" },
        ["Aluminum"] = new[] { "アルミニウム", "アルミ" },
        ["Tungsten"] = new[] { "タングステン" },
        ["Diamond"] = new[] { "ダイヤモンド" },
        ["Gold"] = new[] { "ゴールド", "金" },
        ["Copper"] = new[] { "銅", "コッパー" },
        ["Iron"] = new[] { "鉄", "アイアン" },
        ["Beryl"] = new[] { "ベリル" },
        ["Corundum"] = new[] { "コランダム" },
        ["Quartz"] = new[] { "クォーツ", "石英" },
        ["Laranite"] = new[] { "ラーナイト" },
        ["Bexalite"] = new[] { "ベキサライト" },
        ["Borase"] = new[] { "ボレース" },
        ["Taranite"] = new[] { "タラナイト" },
        ["Hephaestanite"] = new[] { "ヘファスタナイト" },
        ["Quantanium"] = new[] { "クォンタニウム" },
        ["Medical Supplies"] = new[] { "医療用品", "医療資材" },
        ["Processed Food"] = new[] { "加工食品" },
        ["Stims"] = new[] { "スティム", "興奮剤" },
        ["Distilled Spirits"] = new[] { "蒸留酒" },
        ["Maze"] = new[] { "メイズ" },
        ["Neon"] = new[] { "ネオン" },
        ["Astatine"] = new[] { "アスタチン" },
        ["Fluorine"] = new[] { "フッ素", "フルオリン" },
        ["Chlorine"] = new[] { "塩素" },
        ["Iodine"] = new[] { "ヨウ素" },
        ["Recycled Material Composite"] = new[] { "リサイクル物質複合材", "物質複合材" },
        ["Waste"] = new[] { "廃棄物" },
        ["Plasma Fuel"] = new[] { "プラズマ燃料" },
        ["Revenant Tree Pollen"] = new[] { "レヴェナントツリー花粉" },
        ["Altruciatoxin"] = new[] { "アルトルシアトキシン" },
        ["WiDoW"] = new[] { "ウィドウ" },
        ["Slam"] = new[] { "スラム" },
        ["E'tam"] = new[] { "エタム" },
    };

    public void BuildFromTradeService(TradeService tradeService, string? translationDbPath = null)
    {
        _entries.Clear();

        // 1. UEX commodity names (English) + known aliases
        foreach (var (name, id) in tradeService.GetCommodityNameToIdMap())
        {
            var entry = new DictionaryEntry
            {
                CommodityId = id,
                EnglishName = name,
                MatchTexts = new List<string> { name },
            };

            if (KnownAliases.TryGetValue(name, out var aliases))
            {
                entry.JapaneseName = aliases[0];
                foreach (var alias in aliases)
                    entry.MatchTexts.Add(alias);
            }

            _entries.Add(entry);
        }

        // 2. Load additional Japanese translations from translations.db
        if (!string.IsNullOrEmpty(translationDbPath) && File.Exists(translationDbPath))
            LoadJapaneseNames(translationDbPath);
    }

    private void LoadJapaneseNames(string dbPath)
    {
        try
        {
            using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
            db.Open();

            // UEX英語名の集合を作成してIN句で検索
            var uexNames = _entries.Select(e => e.EnglishName).ToHashSet(StringComparer.OrdinalIgnoreCase);

            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
                SELECT english, japanese FROM translations
                WHERE japanese IS NOT NULL AND japanese != ''
                AND english IS NOT NULL AND english != ''
                AND LENGTH(english) >= 3 AND LENGTH(english) <= 50";
            using var reader = cmd.ExecuteReader();

            var enToJa = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
            {
                var en = reader.GetString(0).Trim();
                var ja = reader.GetString(1).Trim();
                if (!string.IsNullOrEmpty(en) && !string.IsNullOrEmpty(ja)
                    && uexNames.Contains(en) && !enToJa.ContainsKey(en))
                    enToJa[en] = ja;
            }

            // Match UEX English names with translations
            foreach (var entry in _entries)
            {
                if (enToJa.TryGetValue(entry.EnglishName, out var ja))
                {
                    entry.JapaneseName = ja;
                    entry.MatchTexts.Add(ja);
                    // Also add without spaces (OCR often inserts spaces in Japanese)
                    var noSpace = ja.Replace(" ", "").Replace("　", "");
                    if (noSpace != ja)
                        entry.MatchTexts.Add(noSpace);
                }
            }
        }
        catch { }
    }

    public (int commodityId, string displayName, double confidence)? FindBestMatch(string ocrText)
    {
        var cleaned = ocrText.Trim().Replace(" ", "").Replace("　", "");
        if (string.IsNullOrEmpty(cleaned) || cleaned.Length < 2) return null;
        if (IsSkipKeyword(ocrText)) return null;

        // Pass 1: Exact match
        foreach (var entry in _entries)
        {
            foreach (var mt in entry.MatchTexts)
            {
                if (mt.Replace(" ", "").Equals(cleaned, StringComparison.OrdinalIgnoreCase))
                    return (entry.CommodityId, entry.DisplayName, 1.0);
            }
        }

        // Pass 2: Contains match
        foreach (var entry in _entries)
        {
            foreach (var mt in entry.MatchTexts)
            {
                var mtClean = mt.Replace(" ", "");
                if (mtClean.Length >= 3 &&
                    (cleaned.Contains(mtClean, StringComparison.OrdinalIgnoreCase) ||
                     mtClean.Contains(cleaned, StringComparison.OrdinalIgnoreCase)))
                    return (entry.CommodityId, entry.DisplayName, 0.9);
            }
        }

        // Pass 3: Levenshtein fuzzy match
        string? bestDisplay = null;
        int bestId = 0;
        int bestDist = int.MaxValue;
        int bestLen = 0;

        foreach (var entry in _entries)
        {
            foreach (var mt in entry.MatchTexts)
            {
                var mtClean = mt.Replace(" ", "");
                var dist = LevenshteinDistance(cleaned, mtClean);
                var threshold = Math.Max(2, mtClean.Length / 3);
                if (dist < bestDist && dist <= threshold)
                {
                    bestDist = dist;
                    bestId = entry.CommodityId;
                    bestDisplay = entry.DisplayName;
                    bestLen = mtClean.Length;
                }
            }
        }

        if (bestDisplay != null)
        {
            var maxLen = Math.Max(cleaned.Length, bestLen);
            var confidence = maxLen > 0 ? 1.0 - (double)bestDist / maxLen : 0;
            if (confidence >= 0.5)
                return (bestId, bestDisplay, confidence);
        }

        return null;
    }

    private static bool IsSkipKeyword(string text)
    {
        var cleaned = text.Replace(" ", "").Replace("　", "").Trim();
        foreach (var kw in SkipKeywords)
        {
            if (cleaned.Contains(kw.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        // Skip lines that look like SCU values or prices only
        if (System.Text.RegularExpressions.Regex.IsMatch(cleaned, @"^[\d,.\s/SCUscuIJij%]+$"))
            return true;
        return false;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
        if (string.IsNullOrEmpty(b)) return a.Length;

        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;

        for (int i = 1; i <= a.Length; i++)
        for (int j = 1; j <= b.Length; j++)
        {
            var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
        }
        return d[a.Length, b.Length];
    }
}

public class DictionaryEntry
{
    public int CommodityId { get; set; }
    public string EnglishName { get; set; } = "";
    public string? JapaneseName { get; set; }
    public List<string> MatchTexts { get; set; } = new();
    public string DisplayName => JapaneseName ?? EnglishName;
}
