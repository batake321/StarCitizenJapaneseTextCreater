using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

public static class IniMerger
{
    public static Dictionary<string, string> Merge(
        Dictionary<string, string> english,
        Dictionary<string, string> japanese,
        string translatedJsonlPath,
        List<string> forceEnglishPatterns,
        string? dbPath = null,
        List<(string English, string Japanese)>? glossary = null,
        string? gamedataDbPath = null,
        bool missionReputationMarks = false,
        bool equipmentClassMarks = true,
        bool blueprintMissionMarks = true)
    {
        var forceRegex = forceEnglishPatterns.Select(p => new Regex(p)).ToList();

        // Load translations: DB takes priority over JSONL
        var translations = new Dictionary<string, string>();

        // First load JSONL
        if (File.Exists(translatedJsonlPath))
        {
            foreach (var line in File.ReadLines(translatedJsonlPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var key = doc.RootElement.GetProperty("key").GetString() ?? "";
                    var ja = doc.RootElement.GetProperty("ja").GetString() ?? "";
                    if (key.Length > 0 && ja.Length > 0)
                        translations[key] = ja;
                }
                catch { }
            }
            Console.WriteLine($"  JSONL translations loaded: {translations.Count}");
        }

        // Then overlay DB (manual edits and CSV imports take priority)
        if (dbPath != null && File.Exists(dbPath))
        {
            using var db = new TranslationDatabase(dbPath);
            var dbTranslations = db.GetAllTranslations();
            foreach (var (key, ja) in dbTranslations)
                translations[key] = ja;
            Console.WriteLine($"  DB translations loaded: {dbTranslations.Count}");
        }

        var merged = new Dictionary<string, string>();
        int enForced = 0, jaTranslated = 0, jaOfficial = 0, enFallback = 0;

        foreach (var (key, enVal) in english)
        {
            if (forceRegex.Any(r => r.IsMatch(key)))
            {
                merged[key] = enVal;
                enForced++;
            }
            else if (translations.TryGetValue(key, out var trVal) && !string.IsNullOrWhiteSpace(trVal))
            {
                merged[key] = trVal;
                jaTranslated++;
            }
            else if (japanese.TryGetValue(key, out var jaVal) && !string.IsNullOrWhiteSpace(jaVal))
            {
                merged[key] = jaVal;
                jaOfficial++;
            }
            else
            {
                merged[key] = enVal;
                enFallback++;
            }
        }

        foreach (var (key, val) in japanese)
        {
            if (!merged.ContainsKey(key))
                merged[key] = val;
        }

        // Clean up "TRANSLATION NOT FOUND" errors from official Japanese file
        int cleaned = 0;
        var keysToFix = merged.Where(kv =>
            kv.Value.Contains("TRANSLATION NOT FOUND FOR LOCID:", StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key).ToList();
        foreach (var key in keysToFix)
        {
            if (key.Equals("blank_space", StringComparison.OrdinalIgnoreCase))
                merged[key] = " ";
            else if (english.TryGetValue(key, out var enFallbackVal))
                merged[key] = enFallbackVal;
            else
                merged[key] = " ";
            cleaned++;
        }

        // Apply glossary replacements to all merged values
        int glossaryFixed = 0;
        if (glossary != null && glossary.Count > 0)
        {
            var keys = merged.Keys.ToList();
            foreach (var key in keys)
            {
                var val = merged[key];
                var newVal = val;
                foreach (var (en, ja) in glossary)
                    newVal = newVal.Replace(en, ja, StringComparison.OrdinalIgnoreCase);
                if (newVal != val)
                {
                    merged[key] = newVal;
                    glossaryFixed++;
                }
            }
        }

        Console.WriteLine($"  Merged: {merged.Count} entries");
        Console.WriteLine($"    English forced (ship/location): {enForced}");
        Console.WriteLine($"    Translated (AI/manual/CSV): {jaTranslated}");
        Console.WriteLine($"    Official Japanese: {jaOfficial}");
        Console.WriteLine($"    English fallback: {enFallback}");
        if (cleaned > 0)
            Console.WriteLine($"    TRANSLATION NOT FOUND cleaned: {cleaned}");
        if (glossaryFixed > 0)
            Console.WriteLine($"    Glossary applied: {glossaryFixed}");

        // Include translation DB keys not present in english extraction
        // (covers new keys from patches applied before re-extraction)
        int dbExtras = 0;
        foreach (var (key, val) in translations)
        {
            if (!merged.ContainsKey(key) && !string.IsNullOrWhiteSpace(val))
            {
                merged[key] = val;
                dbExtras++;
            }
        }
        if (dbExtras > 0)
            Console.WriteLine($"    DB-only keys added (not in extraction): {dbExtras}");

        // 装備名に分類の印 (例: "Bracer <EM4>[軍1C]</EM4>")、設計図がもらえるミッション名に "[BP]" を付ける。
        // ",P" 複製・"@" 前置複製より前に行うこと (印を付けたあとの値を複製に載せるため)
        if (equipmentClassMarks)
        {
            var componentMarks = AnnotateComponentNames(english, merged);
            if (componentMarks > 0)
                Console.WriteLine($"    Component class marks: {componentMarks}");
        }
        if (missionReputationMarks && !string.IsNullOrEmpty(gamedataDbPath))
        {
            var repMarks = AnnotateMissionReputation(english, merged, gamedataDbPath);
            if (repMarks > 0)
                Console.WriteLine($"    Mission reputation marks: {repMarks}");
        }
        if (blueprintMissionMarks)
        {
            var blueprintMarks = AnnotateBlueprintMissions(english, merged);
            if (blueprintMarks > 0)
                Console.WriteLine($"    Blueprint mission marks: {blueprintMarks}");
        }

        // Strip ",P" suffix variants — game references keys without the parameter tag
        int paramStripped = 0;
        foreach (var key in merged.Keys.ToList())
        {
            if (key.EndsWith(",P", StringComparison.Ordinal))
            {
                var baseKey = key[..^2];
                if (!merged.ContainsKey(baseKey))
                {
                    merged[baseKey] = merged[key];
                    paramStripped++;
                }
            }
        }
        if (paramStripped > 0)
            Console.WriteLine($"    ,P suffix stripped duplicates: {paramStripped}");

        // Add @-prefixed duplicates for keys the game may reference with @ prefix
        int atDupes = 0;
        foreach (var key in merged.Keys.ToList())
        {
            var atKey = "@" + key;
            if (!merged.ContainsKey(atKey))
            {
                merged[atKey] = merged[key];
                atDupes++;
            }
        }
        Console.WriteLine($"    @-prefixed duplicates added: {atDupes}");

        return merged;
    }

    // 装備名の後ろに付ける分類の印。<EM4> はゲーム自身がミッション説明で使っている強調タグ
    // (translations の text_ui_tags_EM4_open で <EM4> と定義されている)。
    // ゲーム側の UI スタイルがこのタグを定義していない画面では、タグがそのまま文字として出る可能性がある。
    // その場合はこの 2 つを "" にすれば色なしの印だけになる
    private const string EmphasisOpen = "<EM4>";
    private const string EmphasisClose = "</EM4>";

    // 装備の 5 分類。これ以外の Class の値 (Ballistic / Energy / Melee など武器のダメージ種別) は対象外
    private static readonly Dictionary<string, string> ComponentClassKanji = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Industrial"] = "産",
        ["Military"] = "軍",
        ["Civilian"] = "市",
        ["Stealth"] = "隠",
        ["Competition"] = "競",
    };

    private static readonly Regex ItemClassRegex = new(@"Class:\s*([A-Za-z]+)", RegexOptions.Compiled);
    private static readonly Regex ItemGradeRegex = new(@"Grade:\s*([A-Z])", RegexOptions.Compiled);
    private static readonly Regex ItemSizeRegex = new(@"Size:\s*(\d+)", RegexOptions.Compiled);

    // 英語の item_Desc に書かれている分類・サイズ・グレードを読み、merged の item_Name の後ろに印を付ける。
    // 例: "Bracer" → "Bracer <EM4>[軍1C]</EM4>"。付けた件数を返す
    private static int AnnotateComponentNames(Dictionary<string, string> english, Dictionary<string, string> merged)
    {
        // item_Desc<X> を <X> で引けるようにする (",P" は落とす)
        var descBySuffix = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, val) in english)
        {
            if (!key.StartsWith("item_Desc", StringComparison.OrdinalIgnoreCase)) continue;
            var s = key["item_Desc".Length..];
            if (s.EndsWith(",P", StringComparison.Ordinal)) s = s[..^2];
            if (s.Length > 0 && !descBySuffix.ContainsKey(s)) descBySuffix[s] = val;
        }

        var annotated = 0;
        foreach (var key in merged.Keys.ToList())
        {
            if (!key.StartsWith("item_Name", StringComparison.OrdinalIgnoreCase)) continue;
            var name = merged[key];
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (name.Contains("[産", StringComparison.Ordinal) || name.Contains("[軍", StringComparison.Ordinal)
                || name.Contains("[市", StringComparison.Ordinal) || name.Contains("[隠", StringComparison.Ordinal)
                || name.Contains("[競", StringComparison.Ordinal)) continue;   // 既に付いている

            var s = key["item_Name".Length..];
            if (s.EndsWith(",P", StringComparison.Ordinal)) s = s[..^2];
            // ゲームデータ側で item_Name<X>_SCItem に対する説明が item_Desc<X> になっていることがある。
            // 完全一致で引けないときだけ "_SCItem" の有無を入れ替えて引き直す
            if (!descBySuffix.TryGetValue(s, out var desc) || desc == null)
            {
                const string scItem = "_SCItem";
                var alt = s.EndsWith(scItem, StringComparison.OrdinalIgnoreCase) ? s[..^scItem.Length] : s + scItem;
                if (!descBySuffix.TryGetValue(alt, out desc) || desc == null) continue;
            }

            var mark = BuildComponentMark(desc);
            if (mark == null) continue;
            merged[key] = $"{name} {EmphasisOpen}{mark}{EmphasisClose}";
            annotated++;
        }
        return annotated;
    }

    // 説明文から "[軍1C]" を作る。装備の 5 分類でなければ null。サイズ・グレードは取れた分だけ入れる
    private static string? BuildComponentMark(string description)
    {
        var cm = ItemClassRegex.Match(description);
        if (!cm.Success || !ComponentClassKanji.TryGetValue(cm.Groups[1].Value, out var kanji)) return null;
        var size = ItemSizeRegex.Match(description);
        var grade = ItemGradeRegex.Match(description);
        return $"[{kanji}{(size.Success ? size.Groups[1].Value : "")}{(grade.Success ? grade.Groups[1].Value : "")}]";
    }

    // 設計図 (Blueprint) がもらえるミッションの名前に印を付ける。例: "Small Purchase Order: ..." → "... <EM4>[BP]</EM4>"
    // BP の数値はゲームデータに存在しないので、有無の印だけを付ける。
    // 判定は英語の説明文に blueprint が出てくるかどうか。説明キーの "_Desc" を "_Title" に置き換えたキーが名前
    private static int AnnotateBlueprintMissions(Dictionary<string, string> english, Dictionary<string, string> merged)
    {
        const string mark = "[BP]";
        var annotated = 0;
        foreach (var (key, desc) in english)
        {
            if (desc == null || desc.IndexOf("blueprint", StringComparison.OrdinalIgnoreCase) < 0) continue;
            var baseKey = key.EndsWith(",P", StringComparison.Ordinal) ? key[..^2] : key;
            // 説明キーから名前キーを作る。ふつうは "_Desc" → "_Title"。
            // "_Desc" を持たないキー (TheCollector_Recipes_DrakeClipper 等) は、最後の "_" の前に "_Title" を挟んだものを試す
            var titleKeys = new List<string>();
            var idx = baseKey.LastIndexOf("_Desc", StringComparison.Ordinal);
            if (idx >= 0)
            {
                titleKeys.Add(baseKey[..idx] + "_Title" + baseKey[(idx + "_Desc".Length)..]);
            }
            else
            {
                var us = baseKey.LastIndexOf('_');
                if (us > 0) titleKeys.Add(baseKey[..us] + "_Title" + baseKey[us..]);
            }
            if (titleKeys.Count == 0) continue;

            foreach (var titleKey in titleKeys)
            foreach (var k in new[] { titleKey, titleKey + ",P" })
            {
                if (!merged.TryGetValue(k, out var title) || string.IsNullOrWhiteSpace(title)) continue;
                if (title.Contains(mark, StringComparison.Ordinal)) continue;
                merged[k] = $"{title} {EmphasisOpen}{mark}{EmphasisClose}";
                annotated++;
            }
        }
        return annotated;
    }

    // ミッション名の後ろに派閥と貢献度を付ける。例: "拠点掃討 <EM4>[Headhunters +1000]</EM4>"
    // 派閥名は faction_reputation.display_key (ロケールキー) を merged / english から引く。
    // 表示文字列どうしの突き合わせは一切しない (日本語化されていても正しく紐付くようにするため)。
    // 同じ名前キーを複数のミッションが共有していることが多いので、
    // 値が割れるときは "+50/100/200" と並記し、派閥が割れるときは派閥名を出さない
    private static int AnnotateMissionReputation(Dictionary<string, string> english, Dictionary<string, string> merged, string gamedataDbPath)
    {
        if (string.IsNullOrEmpty(gamedataDbPath) || !File.Exists(gamedataDbPath)) return 0;

        var annotated = 0;
        try
        {
            // ミッションごとの最大貢献度と、その最大値の行の派閥キー
            var missionBest = new Dictionary<string, (long Amount, string? FactionKey)>(StringComparer.Ordinal);
            // 名前キーごとの、配下ミッション
            var titleMissions = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            using (var db = new SqliteConnection($"Data Source={gamedataDbPath};Mode=ReadOnly"))
            {
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = """
                    SELECT mr.title_key, mr.mission_record, fr.display_key, ra.amount
                    FROM mission_reputation mr
                    LEFT JOIN faction_reputation fr ON fr.record_name = mr.faction_record
                    LEFT JOIN reputation_reward_amount ra ON ra.record_name = mr.amount_record
                    WHERE ra.amount > 0
                    """;
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var titleKey = r.IsDBNull(0) ? "" : r.GetString(0);
                    var missionRecord = r.IsDBNull(1) ? "" : r.GetString(1);
                    var factionKey = r.IsDBNull(2) ? null : r.GetString(2);
                    var amount = r.IsDBNull(3) ? 0L : r.GetInt64(3);
                    if (titleKey.Length == 0 || missionRecord.Length == 0 || amount <= 0) continue;

                    // 同点なら最初のものを残す
                    if (!missionBest.TryGetValue(missionRecord, out var best) || amount > best.Amount)
                        missionBest[missionRecord] = (amount, factionKey);

                    if (!titleMissions.TryGetValue(titleKey, out var set))
                        titleMissions[titleKey] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(missionRecord);
                }
            }

            foreach (var (titleKey, missions) in titleMissions)
            {
                var amounts = new SortedSet<long>();
                var factionKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var m in missions)
                {
                    if (!missionBest.TryGetValue(m, out var best)) continue;
                    amounts.Add(best.Amount);
                    if (!string.IsNullOrEmpty(best.FactionKey)) factionKeys.Add(best.FactionKey);
                }
                if (amounts.Count == 0) continue;

                var amountText = "+" + string.Join("/", amounts);

                // 派閥はちょうど 1 つに定まるときだけ出す。名前はロケールキーで引く (文字列比較はしない)
                string? faction = null;
                if (factionKeys.Count == 1)
                {
                    var dk = factionKeys.First();
                    if (merged.TryGetValue(dk, out var fv) && !string.IsNullOrWhiteSpace(fv)) faction = fv;
                    else if (merged.TryGetValue(dk + ",P", out fv) && !string.IsNullOrWhiteSpace(fv)) faction = fv;
                    else if (english.TryGetValue(dk, out fv) && !string.IsNullOrWhiteSpace(fv)) faction = fv;
                    else if (english.TryGetValue(dk + ",P", out fv) && !string.IsNullOrWhiteSpace(fv)) faction = fv;
                }

                var mark = faction != null
                    ? $" {EmphasisOpen}[{faction} {amountText}]{EmphasisClose}"
                    : $" {EmphasisOpen}[{amountText}]{EmphasisClose}";

                foreach (var k in new[] { titleKey, titleKey + ",P" })
                {
                    if (!merged.TryGetValue(k, out var title) || string.IsNullOrWhiteSpace(title)) continue;
                    if (title.Contains(EmphasisOpen + "[", StringComparison.Ordinal)) continue;   // 既に印が付いている
                    merged[k] = title + mark;
                    annotated++;
                }
            }
        }
        catch { }
        return annotated;
    }
}
