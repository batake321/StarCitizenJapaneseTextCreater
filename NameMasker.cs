using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

// AI 翻訳で、文の中の名前 (地名・船名・品名の英語名) を送る前に {name1}、{name2}… に置き換え、返ってきた訳の中で元の英語名に戻す。
// 名前の一覧: gamedata_cache.db の starmap_names.name_key・items.name・item_index.name_key を翻訳 DB で英語に引いたものと ships.name_en。
// 長い名前から先に、単語の境界で完全一致 (大文字小文字を区別) で置き換える。番号は出てきた順、同じ名前は同じ番号。
public static class NameMasker
{
    // 一般の語 (名前の一覧から外す)
    private static readonly HashSet<string> CommonWords = new(StringComparer.Ordinal) { "Station" };

    private static readonly Regex WordRegex = new(@"[\w'"".-]+");
    private static readonly Regex BoundaryCharRegex = new(@"[\w-]");
    private static readonly Regex PossessiveRegex = new("['’]s$");
    private static readonly Regex PlaceholderRegex = new(@"\{name(\d+)\}");
    // 正しい {nameN} を除いた後に残る、壊れた置き換え記号 ({Name1}・{name 1}・{name1・name1} など)
    private static readonly Regex BrokenPlaceholderRegex = new(@"\{\s*name|name\s*\d+\s*\}", RegexOptions.IgnoreCase);

    // 先頭の語 → その語で始まる名前 (長い順)
    private static Dictionary<string, List<string>> _index = new(StringComparer.Ordinal);

    public static int Count { get; private set; }

    // 品名として扱わない品目の種類 (item_type)。座席・扉・エレベーター・画面・操作盤・照明・貨物グリッドなど船の設備・飾り・器の類
    private static readonly HashSet<string> NonProductItemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Seat", "SeatAccess", "SeatDashboard", "Door", "Elevator", "Usable", "Misc", "Room", "AttachedPart", "CargoGrid",
        "Light", "DockingCollar", "DockingAnimator", "AirTrafficController", "Display", "ControlPanel", "Player", "Container",
        "LandingSystem", "LandingGear", "Decal", "Flair_Surface", "FPS_Deployable",
    };

    // 地名として扱わない星図の場所 (starmap_names.record_name。大文字小文字無視):
    //   雛形 (Template を含む)・ビーコン (Beacon を含む)・ミッション用 (StarMapObject. の後が MISSION_ で始まる)
    private static readonly string[] NonPlaceRecordParts = ["Template", "Beacon"];
    private const string MissionRecordPrefix = "MISSION_";

    // 取り付け口の名前のキー (先頭の "@" を除いて itemport_ で始まる。例 itemport_body_itemport → Body) か。品名にも地名にも使わない
    private static bool IsItemPortKey(string key) =>
        key.Trim().TrimStart('@').StartsWith("itemport_", StringComparison.OrdinalIgnoreCase);

    // 品目の種類の名前 (items.item_type・item_sub_type、item_index.item_type・sub_type)。英語名がこれそのもの (例 Turret・Radar) の名前は使わない
    private static HashSet<string> ReadItemTypeNames(SqliteConnection game)
    {
        var typeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (table, column) in new[] { ("items", "item_type"), ("items", "item_sub_type"), ("item_index", "item_type"), ("item_index", "sub_type") })
        {
            if (!HasColumn(game, table, column)) continue;
            using var c = game.CreateCommand();
            c.CommandText = $"SELECT DISTINCT {column} FROM {table} WHERE {column} IS NOT NULL AND {column} != ''";
            using var r = c.ExecuteReader();
            while (r.Read()) typeNames.Add(r.GetString(0).Trim());
        }
        return typeNames;
    }

    private static bool HasColumn(SqliteConnection game, string table, string column)
    {
        using var c = game.CreateCommand();
        c.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = @c";
        c.Parameters.AddWithValue("@c", column);
        return c.ExecuteScalar() != null;
    }

    // 地名の名前のキーを gamedata_cache.db の starmap_names.name_key から読む (先頭の "@" はそのまま)。次のものは除く:
    //   ・本物の場所でないもの (NonPlaceRecordParts・MissionRecordPrefix)
    //   ・取り付け口の名前のキー (IsItemPortKey)
    //   ・英語名 (englishOf で引く) が品目の種類の名前そのもの (品名と同じ判定)
    // AI 翻訳の名前の置き換え (Load) と、名前の英語への書き戻し (TranslationDatabase.RevertNamesToEnglish) の両方で使う。表・列が無ければ空
    public static List<string> ReadPlaceNameKeys(SqliteConnection game, Func<string, string?> englishOf)
    {
        var result = new List<string>();
        if (!HasColumn(game, "starmap_names", "name_key")) return result;
        var typeNames = ReadItemTypeNames(game);
        using var c = game.CreateCommand();
        c.CommandText = "SELECT record_name, name_key FROM starmap_names WHERE name_key IS NOT NULL AND name_key != ''";
        using var r = c.ExecuteReader();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        while (r.Read())
        {
            var record = r.IsDBNull(0) ? "" : r.GetString(0);
            if (NonPlaceRecordParts.Any(p => record.Contains(p, StringComparison.OrdinalIgnoreCase))) continue;
            var dot = record.IndexOf('.');
            if (record[(dot + 1)..].StartsWith(MissionRecordPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var key = r.GetString(1);
            if (IsItemPortKey(key)) continue;
            var en = englishOf(key);
            if (en != null && typeNames.Contains(en.Trim())) continue;
            if (keys.Add(key)) result.Add(key);
        }
        return result;
    }

    // 品名 (武器・装備) の名前のキーを gamedata_cache.db の items.name と item_index.name_key から読む (先頭の "@" はそのまま)。
    // 次のものは品名として扱わない:
    //   ・種類 (item_type) が NonProductItemTypes のもの
    //   ・英語名 (englishOf で引く) が品目の種類の名前そのもの (item_type・sub_type のどれか。大文字小文字無視。例 Turret・Radar)
    //   ・取り付け口の名前のキー (IsItemPortKey)
    // AI 翻訳の名前の置き換え (Load) と、名前の英語への書き戻し (TranslationDatabase.RevertNamesToEnglish) の両方で使う。
    // 表・列が無ければ飛ばす (item_type の列が無ければ種類では分けない)
    public static List<string> ReadProductNameKeys(SqliteConnection game, Func<string, string?> englishOf)
    {
        var typeNames = ReadItemTypeNames(game);
        var result = new List<string>();
        foreach (var (table, column) in new[] { ("items", "name"), ("item_index", "name_key") })
        {
            if (!HasColumn(game, table, column)) continue;
            var typeCol = HasColumn(game, table, "item_type") ? "item_type" : "NULL";
            using var c = game.CreateCommand();
            c.CommandText = $"SELECT DISTINCT {column}, {typeCol} FROM {table} WHERE {column} IS NOT NULL AND {column} != ''";
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                if (!r.IsDBNull(1) && NonProductItemTypes.Contains(r.GetString(1))) continue;
                var key = r.GetString(0);
                if (IsItemPortKey(key)) continue;
                var en = englishOf(key);
                if (en != null && typeNames.Contains(en.Trim())) continue;
                result.Add(key);
            }
        }
        return result;
    }

    // 名前の一覧を作る。返り値: 名前の数。gamedata_cache.db・翻訳 DB が無ければ 0 (置き換えをしない)
    public static int Load(string translationDbPath, string gamedataDbPath)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (File.Exists(translationDbPath) && File.Exists(gamedataDbPath))
        {
            var en = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var t = new SqliteConnection($"Data Source={translationDbPath};Mode=ReadOnly"))
            {
                t.Open();
                using var c = t.CreateCommand();
                c.CommandText = "SELECT key, english FROM translations WHERE english IS NOT NULL";
                using var r = c.ExecuteReader();
                while (r.Read()) en[r.GetString(0)] = r.GetString(1);
            }
            string? Lookup(string? key)
            {
                if (string.IsNullOrEmpty(key)) return null;
                key = key.TrimStart('@');
                return en.TryGetValue(key, out var v) ? v : null;
            }
            using var g = new SqliteConnection($"Data Source={gamedataDbPath};Mode=ReadOnly");
            g.Open();
            bool HasColumn(string table, string column)
            {
                using var c = g.CreateCommand();
                c.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = @c";
                c.Parameters.AddWithValue("@c", column);
                return c.ExecuteScalar() != null;
            }
            void Read(string table, string column, bool viaTranslation)
            {
                if (!HasColumn(table, column)) return;
                using var c = g.CreateCommand();
                c.CommandText = $"SELECT {column} FROM {table} WHERE {column} IS NOT NULL AND {column} != ''";
                using var r = c.ExecuteReader();
                while (r.Read())
                {
                    var v = viaTranslation ? Lookup(r.GetString(0)) : r.GetString(0);
                    if (v != null) names.Add(v.Trim());
                }
            }
            // 地名は本物の場所だけ (ReadPlaceNameKeys)
            foreach (var k in ReadPlaceNameKeys(g, Lookup))
            {
                var v = Lookup(k);
                if (v != null) names.Add(v.Trim());
            }
            Read("ships", "name_en", false);
            // 品名は設備の類でない品目だけ (ReadProductNameKeys)
            foreach (var k in ReadProductNameKeys(g, Lookup))
            {
                var v = Lookup(k);
                if (v != null) names.Add(v.Trim());
            }
        }
        // 名前として使えないもの (短すぎる・仮の文字・改行や ~ % を含む) と一般の語を外す
        names.RemoveWhere(n => n.Length < 3 || n.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase)
                               || n.Contains('\n') || n.Contains('~') || n.Contains('%') || CommonWords.Contains(n));

        var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var n in names)
        {
            var m = WordRegex.Match(n);
            if (!m.Success || m.Index != 0) continue;   // 名前の先頭が語で始まらないものは引かない
            if (!index.TryGetValue(m.Value, out var list)) index[m.Value] = list = [];
            list.Add(n);
        }
        foreach (var list in index.Values) list.Sort((a, b) => b.Length.CompareTo(a.Length));
        _index = index;
        Count = names.Count;
        return Count;
    }

    private static IEnumerable<string> Variants(string w)
    {
        var set = new HashSet<string>(StringComparer.Ordinal)
        {
            w, w.TrimEnd('.', ',', '\'', '"'), w.Trim('"', '\''), PossessiveRegex.Replace(w, "")
        };
        return set;
    }

    // 名前を {nameN} に置き換える。返り値: 置き換えた文と、"nameN" → 元の名前。名前が無ければ元の文と空の表
    // 原文にもともと "{name" があるときは置き換えない (戻すときに区別できないため)
    public static (string Masked, Dictionary<string, string> Map) Mask(string english)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_index.Count == 0 || english.Contains("{name", StringComparison.OrdinalIgnoreCase)) return (english, map);

        var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
        var sb = new StringBuilder();
        int pos = 0;
        foreach (Match m in WordRegex.Matches(english))
        {
            var s = m.Index;
            if (s < pos) continue;
            if (s > 0 && BoundaryCharRegex.IsMatch(english[s - 1].ToString())) continue;
            var cands = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in Variants(m.Value))
                if (_index.TryGetValue(key, out var list)) cands.UnionWith(list);
            foreach (var n in cands.OrderByDescending(c => c.Length))
            {
                if (s + n.Length > english.Length || string.CompareOrdinal(english, s, n, 0, n.Length) != 0) continue;
                var e = s + n.Length;
                if (e < english.Length && BoundaryCharRegex.IsMatch(english[e].ToString())) continue;
                if (!numbers.TryGetValue(n, out var no))
                {
                    numbers[n] = no = numbers.Count + 1;
                    map[$"name{no}"] = n;
                }
                sb.Append(english, pos, s - pos).Append("{name").Append(no).Append('}');
                pos = e;
                break;
            }
        }
        if (map.Count == 0) return (english, map);
        sb.Append(english, pos, english.Length - pos);
        return (sb.ToString(), map);
    }

    // 訳の中の {nameN} を元の名前に戻す。{nameN} が欠けている・余計にある・番号が壊れているときは null
    // (置き換えた文の番号がどれも 1 回以上あり、置き換えた文に無い番号が無いことを確かめる。同じ名前が原文に 2 回出て訳で 1 回になったものは通す)
    public static string? Unmask(string masked, string translated, Dictionary<string, string> map)
    {
        if (map.Count == 0) return translated;
        var expected = PlaceholderRegex.Matches(masked).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
        var actual = PlaceholderRegex.Matches(translated).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
        if (!expected.SetEquals(actual))
            return null;
        if (BrokenPlaceholderRegex.IsMatch(PlaceholderRegex.Replace(translated, "")))
            return null;
        return PlaceholderRegex.Replace(translated, m => map[$"name{m.Groups[1].Value}"]);
    }
}
