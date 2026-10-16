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
            var enToJa = LoadTranslatedNames(dbPath, _entries.Select(e => e.EnglishName));

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

    private const string CommodityKeyPrefix = "items_commodities_";

    // 英語名の一致で訳を拾うとき、使わない行のキーの頭 (人名・依頼の品・依頼の文・船の名前・ボタンなど。
    // 例: Beryl → Human_First_Names_F_0296 の 'Beryl'、Quartz → Mission_Item_0176 の '石英'、Scrap → ShipSelector_Incinerate,P の '廃棄')
    private static readonly string[] ExcludedKeyPrefixes =
    {
        "Human_First_Names_", "Human_Surnames_", "Mission_Item_", "mg_", "Event_ShipName_", "ShipSelector_", "vehicle_Name",
    };

    // UEX の商品名 → items_commodities_ の後ろの形。小文字にし、末尾の " (Ore)" → "_ore"・" (Raw)" → "_raw"、英数字と _ 以外を除く
    // (例: "Laranite" → laranite、"Borase (Ore)" → borase_ore、"Audio-Visual Equipment" → audiovisualequipment、"E'tam" → etam)
    private static string ToCommodityKeySuffix(string englishName)
    {
        var s = englishName.Trim().ToLowerInvariant();
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*\(ore\)$", "_ore");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*\(raw\)$", "_raw");
        return System.Text.RegularExpressions.Regex.Replace(s, @"[^a-z0-9_]", "");
    }

    // translations.db のキーの後ろの形。大文字小文字を区別せず、末尾の ",P" を除いて比べる (例: items_commodities_iron,P → iron)
    private static string NormalizeCommodityKeySuffix(string key)
    {
        var s = key.Substring(CommodityKeyPrefix.Length);
        if (s.EndsWith(",P", StringComparison.Ordinal)) s = s[..^2];
        return s.ToLowerInvariant();
    }

    // 商品の英語名ごとの訳 (translations.db)。引き方:
    //   1. items_commodities_<名前> のキー (ToCommodityKeySuffix の形)
    //   2. 英語名の一致。items_commodities_ の行を優先し、ExcludedKeyPrefixes の行は使わない
    // どちらにも無い英語名は返さない (KnownAliases は呼ぶ側で使う)
    private static Dictionary<string, string> LoadTranslatedNames(string dbPath, IEnumerable<string> englishNames)
    {
        var names = englishNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        db.Open();

        // 1. items_commodities_<名前> のキー
        var byKey = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT key, japanese FROM translations
                WHERE key LIKE 'items_commodities_%'
                AND japanese IS NOT NULL AND japanese != ''";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var key = reader.GetString(0);
                if (!key.StartsWith(CommodityKeyPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                var ja = reader.GetString(1).Trim();
                if (string.IsNullOrEmpty(ja)) continue;
                byKey.TryAdd(NormalizeCommodityKeySuffix(key), ja);
            }
        }
        foreach (var name in names)
        {
            if (byKey.TryGetValue(ToCommodityKeySuffix(name), out var ja))
                result[name] = ja;
        }

        // 2. 英語名の一致 (キーで引けなかったものだけ)
        var rest = names.Where(n => !result.ContainsKey(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (rest.Count == 0) return result;

        var commodityRows = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var otherRows = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT key, english, japanese FROM translations
                WHERE japanese IS NOT NULL AND japanese != ''
                AND english IS NOT NULL AND english != ''
                AND LENGTH(english) >= 3 AND LENGTH(english) <= 50";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var key = reader.GetString(0);
                var en = reader.GetString(1).Trim();
                var ja = reader.GetString(2).Trim();
                if (string.IsNullOrEmpty(en) || string.IsNullOrEmpty(ja) || !rest.Contains(en)) continue;
                if (key.StartsWith(CommodityKeyPrefix, StringComparison.OrdinalIgnoreCase))
                    commodityRows.TryAdd(en, ja);
                else if (!ExcludedKeyPrefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    otherRows.TryAdd(en, ja);
            }
        }
        foreach (var name in rest)
        {
            if (commodityRows.TryGetValue(name, out var ja) || otherRows.TryGetValue(name, out ja))
                result[name] = ja;
        }
        return result;
    }

    // 商品の英語名ごとの日本語名。LoadTranslatedNames で引き、無ければ KnownAliases の最初の名前。どちらにも無い英語名は返さない
    // (価格キャプチャの DictionaryEntry.JapaneseName と同じ決め方)
    public static Dictionary<string, string> GetJapaneseNames(string? translationDbPath, IEnumerable<string> englishNames)
    {
        var names = englishNames.ToList();
        Dictionary<string, string> result;
        try
        {
            result = !string.IsNullOrEmpty(translationDbPath) && File.Exists(translationDbPath)
                ? LoadTranslatedNames(translationDbPath, names)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch { result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }

        foreach (var name in names)
        {
            if (!result.ContainsKey(name) && KnownAliases.TryGetValue(name, out var aliases))
                result[name] = aliases[0];
        }
        return result;
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
