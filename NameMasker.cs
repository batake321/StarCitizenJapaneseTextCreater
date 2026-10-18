using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

// AI 翻訳で、文の中の名前 (地名・船名・品名の英語名) を送る前に {name1}、{name2}… に置き換え、返ってきた訳の中で元の英語名に戻す。
// 名前の一覧: gamedata_cache.db の starmap_names.name_key・items.name・item_index.name_key を翻訳 DB で英語に引いたものと ships.name_en と、翻訳 DB の person_names (AI が返した人名を貯めたもの。AddPersonNames)。
// 長い名前から先に、単語の境界で完全一致 (大文字小文字を区別) で置き換える。番号は出てきた順、同じ名前は同じ番号。
public static class NameMasker
{
    // 一般の語 (名前の一覧から外す)
    private static readonly HashSet<string> CommonWords = new(StringComparer.Ordinal) { "Station" };

    private static readonly Regex WordRegex = new(@"[\w'"".-]+");
    private static readonly Regex BoundaryCharRegex = new(@"[\w-]");
    private static readonly Regex PossessiveRegex = new("['’]s$");
    private static readonly Regex PlaceholderRegex = new(@"\{name(\d+)\}");
    // 名前の直後に続く大文字始まりの英単語 (Mask。\G で指定位置から)
    private static readonly Regex FollowingCapitalWordRegex = new(@"\G [A-Z][A-Za-z]*");
    // 正しい {nameN} を除いた後に残る、壊れた置き換え記号 ({Name1}・{name 1}・{name1・name1} など)
    private static readonly Regex BrokenPlaceholderRegex = new(@"\{\s*name|name\s*\d+\s*\}", RegexOptions.IgnoreCase);

    // 先頭の語 → その語で始まる名前 (長い順)
    private static Dictionary<string, List<string>> _index = new(StringComparer.Ordinal);
    // 今の名前の一覧 (AddPersonNames で足すときの元)。_index・_names は差し替えるだけで中身は書き換えない (Mask は差し替え前後どちらかの表を読む)
    private static HashSet<string> _names = new(StringComparer.Ordinal);
    private static readonly object _lock = new();
    // 翻訳 DB の道 (Load で受け取ったもの。AddPersonNames が person_names に書く先)
    private static string? _translationDbPath;

    public static int Count { get; private set; }

    // 品名として扱わない品目の種類 (item_type)。座席・扉・エレベーター・画面・操作盤・照明・貨物グリッドなど船の設備・飾り・器の類
    private static readonly HashSet<string> NonProductItemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Seat", "SeatAccess", "SeatDashboard", "Door", "Elevator", "Usable", "Misc", "Room", "AttachedPart", "CargoGrid",
        "Light", "DockingCollar", "DockingAnimator", "AirTrafficController", "Display", "ControlPanel", "Player", "Container",
        "LandingSystem", "LandingGear", "Decal", "Flair_Surface", "FPS_Deployable",
    };

    // 地名として扱わない星図の場所 (starmap_names.record_name。大文字小文字無視):
    //   ビーコン (Beacon を含む)・ミッション用 (StarMapObject. の後が MISSION_ で始まる)
    // 雛形 (Template を含む) は外さない。雛形にも本物の場所の名前が付いたものがある (例 OrbitalPlatform_TEMPLATE → Pyro IV Abandoned Outpost)
    private static readonly string[] NonPlaceRecordParts = ["Beacon"];
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

    // 英語名が仮の文字か: 空 (空白だけ)・「<= … =>」の形 (例 <= UNINITIALIZED =>)・PLACEHOLDER を含む (大文字小文字無視。Load の除外と同じ)。
    // 英語名が引けない (null) ものは仮の文字として扱わない
    private static bool IsPlaceholderName(string? en)
    {
        if (en == null) return false;
        var t = en.Trim();
        return t.Length == 0 || (t.StartsWith("<=") && t.EndsWith("=>")) || t.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase);
    }

    // 名前 (地名・船名・武器・装備) のキーを読む。名前の英語への書き戻し (TranslationDatabase.RevertNamesToEnglish) と、
    // AI に送らない行の判定 (TranslationOrchestrator.BuildUntranslatedList) の両方で使う。
    //   地名: ReadPlaceNameKeys、船名: ships.name (列が無ければ飛ばす)、武器・装備: ReadProductNameKeys
    // キーは前後の空白と先頭の "@" を除いたもの (translations.key と大文字小文字を区別せずに照合する)。
    // 空のものと、"desc" を含むもの (説明文。items.name に説明文のキーが入っている品目がある)、英語名が仮の文字のもの (IsPlaceholderName) は除く
    public static (List<string> Places, List<string> Ships, List<string> Items) ReadNameKeys(SqliteConnection game, Func<string, string?> englishOf)
    {
        List<string> Normalize(IEnumerable<string> raw)
        {
            var list = new List<string>();
            foreach (var r in raw)
            {
                var k = r.Trim();
                if (k.StartsWith('@')) k = k[1..];
                if (k.Length == 0 || k.Contains("desc", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsPlaceholderName(englishOf(k))) continue;
                list.Add(k);
            }
            return list;
        }
        var ships = new List<string>();
        if (HasColumn(game, "ships", "name"))
        {
            using var c = game.CreateCommand();
            c.CommandText = "SELECT DISTINCT name FROM ships WHERE name IS NOT NULL AND name != ''";
            using var r = c.ExecuteReader();
            while (r.Read()) ships.Add(r.GetString(0));
        }
        return (Normalize(ReadPlaceNameKeys(game, englishOf)), Normalize(ships), Normalize(ReadProductNameKeys(game, englishOf)));
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
        // 貯めた人名 (翻訳 DB の person_names。AddPersonNames で足したもの)。gamedata_cache.db が無くても読む
        _translationDbPath = translationDbPath;
        if (File.Exists(translationDbPath))
        {
            using var t = new SqliteConnection($"Data Source={translationDbPath};Mode=ReadOnly");
            t.Open();
            foreach (var p in TranslationDatabase.ReadPersonNames(t)) names.Add(p.Trim());
        }
        // 名前として使えないもの (短すぎる・仮の文字・改行や ~ % を含む) と一般の語を外す
        names.RemoveWhere(n => !IsUsableName(n));

        lock (_lock)
        {
            _names = names;
            _index = BuildIndex(names);
            Count = names.Count;
            return Count;
        }
    }

    // 名前の一覧に入れられるか (Load の除外と同じ): 3 文字以上、仮の文字・改行・~・% を含まない、一般の語でない
    private static bool IsUsableName(string n) =>
        !(n.Length < 3 || n.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase)
          || n.Contains('\n') || n.Contains('~') || n.Contains('%') || CommonWords.Contains(n));

    private static Dictionary<string, List<string>> BuildIndex(IEnumerable<string> names)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var n in names)
        {
            var m = WordRegex.Match(n);
            if (!m.Success || m.Index != 0) continue;   // 名前の先頭が語で始まらないものは引かない
            if (!index.TryGetValue(m.Value, out var list)) index[m.Value] = list = [];
            list.Add(n);
        }
        foreach (var list in index.Values) list.Sort((a, b) => b.Length.CompareTo(a.Length));
        return index;
    }

    // 人名の英語の形: 半角空白 1 つで区切った語がどれも英大文字で始まり、英字・' ’ . - だけでできている (例 John・Miles Eckhart・O'Neil)
    private static readonly Regex PersonNameShapeRegex = new(@"^[A-Z][A-Za-z'’.\-]*(?: [A-Z][A-Za-z'’.\-]*)*$");

    // AI が返した人名を貯めてよいか。条件:
    //   ・名前の一覧に入れられる (IsUsableName。3 文字以上・一般の語 (CommonWords) でない など)
    //   ・人名の英語の形 (PersonNameShapeRegex。{nameN}・日本語・数字を含むものは外れる)
    //   ・原文 (置き換える前の英語) に、単語の境界で大文字小文字を区別して出てくる
    public static bool IsStorablePersonName(string name, string english)
    {
        if (!IsUsableName(name) || !PersonNameShapeRegex.IsMatch(name)) return false;
        for (int i = english.IndexOf(name, StringComparison.Ordinal); i >= 0; i = english.IndexOf(name, i + 1, StringComparison.Ordinal))
        {
            var e = i + name.Length;
            if ((i == 0 || !BoundaryCharRegex.IsMatch(english[i - 1].ToString()))
                && (e >= english.Length || !BoundaryCharRegex.IsMatch(english[e].ToString())))
                return true;
        }
        return false;
    }

    // AI が返した人名 ((人名, その訳のキー)。IsStorablePersonName で確かめたもの) を名前の一覧に足し、翻訳 DB の person_names に貯める
    // (次の Mask から {nameN} に置き換える)。一覧に既にある名前は足さない。返り値: 新しく足した数。
    // 翻訳 DB が無い (Load に空の道を渡した) ときは一覧にだけ足す
    public static int AddPersonNames(IEnumerable<(string Name, string SourceKey)> persons)
    {
        var added = new List<(string Name, string SourceKey)>();
        lock (_lock)
        {
            var names = new HashSet<string>(_names, StringComparer.Ordinal);
            foreach (var (n, k) in persons)
                if (IsUsableName(n) && names.Add(n)) added.Add((n, k));
            if (added.Count == 0) return 0;
            _names = names;
            _index = BuildIndex(names);
            Count = names.Count;
        }
        var path = _translationDbPath;
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            using var db = new TranslationDatabase(path);
            db.AddPersonNames(added);
        }
        return added.Count;
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
        var index = _index;   // AddPersonNames が差し替えても、この文の間は同じ表を使う
        if (index.Count == 0 || english.Contains("{name", StringComparison.OrdinalIgnoreCase)) return (english, map);

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
                if (index.TryGetValue(key, out var list)) cands.UnionWith(list);
            foreach (var n in cands.OrderByDescending(c => c.Length))
            {
                if (s + n.Length > english.Length || string.CompareOrdinal(english, s, n, 0, n.Length) != 0) continue;
                var e = s + n.Length;
                if (e < english.Length && BoundaryCharRegex.IsMatch(english[e].ToString())) continue;
                // 名前のすぐ後に大文字で始まる英単語 (半角空白 1 つ + A-Z で始まり英字だけが続く語。直後が英字・数字・_・- でない) が
                // 続くときは、続く語をすべて含めて 1 つの名前にする (例 一覧に Crusader だけがあるとき "Crusader Industries" をまとめる)
                while (FollowingCapitalWordRegex.Match(english, e) is { Success: true } fw
                       && (fw.Index + fw.Length >= english.Length || !BoundaryCharRegex.IsMatch(english[fw.Index + fw.Length].ToString())))
                    e = fw.Index + fw.Length;
                var name = english[s..e];
                if (!numbers.TryGetValue(name, out var no))
                {
                    numbers[name] = no = numbers.Count + 1;
                    map[$"name{no}"] = name;
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
        translated = RestoreSpacesAroundPlaceholders(masked, translated);
        return PlaceholderRegex.Replace(translated, m => map[$"name{m.Groups[1].Value}"]);
    }

    private static bool IsAsciiLetterOrDigit(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9');

    // s[i] が英字・数字か。ただし「\」の直後の文字 (文字列としての「\n」の「n」など) は英字として扱わない
    private static bool IsWordCharAt(string s, int i) => IsAsciiLetterOrDigit(s[i]) && !(i > 0 && s[i - 1] == '\\');

    // 置き換えた文で {nameN} の直前 (直後) が「英字・数字 + 空白」または「ほかの {nameN} + 空白」だったのに、
    // 訳で空白が消えて英字・数字・ほかの {nameN} と直接つながっているときは、置き換えた文と同じ空白を入れる
    // (例 置き換えた文 "Amare {name3}"、訳 "Amare{name3}" → "Amare {name3}"。戻すと "Amare Jericho"。
    //  置き換えた文 "{name1} {name2}"、訳 "{name1}{name2}" → "{name1} {name2}")。
    // 同じ番号が置き換えた文に何回か出て空白が違うときは、最初に見つかった空白を使う
    private static string RestoreSpacesAroundPlaceholders(string masked, string translated)
    {
        var maskedMatches = PlaceholderRegex.Matches(masked);
        var maskedEnds = maskedMatches.Select(x => x.Index + x.Length).ToHashSet();
        var maskedStarts = maskedMatches.Select(x => x.Index).ToHashSet();
        // キー: ({nameN}, 隣がほかの {nameN} か)。隣が英字・数字のときと {nameN} のときを分けて覚え、訳でも同じ種類の隣のときだけ入れる
        var before = new Dictionary<(string, bool), string>();
        var after = new Dictionary<(string, bool), string>();
        foreach (Match m in maskedMatches)
        {
            int i = m.Index;
            while (i > 0 && char.IsWhiteSpace(masked[i - 1])) i--;
            if (i < m.Index && i > 0 && (IsWordCharAt(masked, i - 1) || maskedEnds.Contains(i)))
                before.TryAdd((m.Value, maskedEnds.Contains(i)), masked[i..m.Index]);
            int e = m.Index + m.Length, j = e;
            while (j < masked.Length && char.IsWhiteSpace(masked[j])) j++;
            if (j > e && j < masked.Length && (IsWordCharAt(masked, j) || maskedStarts.Contains(j)))
                after.TryAdd((m.Value, maskedStarts.Contains(j)), masked[e..j]);
        }
        if (before.Count == 0 && after.Count == 0) return translated;
        var trMatches = PlaceholderRegex.Matches(translated);
        var trEnds = trMatches.Select(x => x.Index + x.Length).ToHashSet();
        var trStarts = trMatches.Select(x => x.Index).ToHashSet();
        var sb = new StringBuilder(translated.Length + 8);
        int pos = 0, insertedAfterAt = -1;
        foreach (Match m in trMatches)
        {
            sb.Append(translated, pos, m.Index - pos);
            // 直前の {nameN} の後ろに空白を入れたところには、前にもう一度入れない
            if (m.Index > 0 && m.Index != insertedAfterAt
                && (IsWordCharAt(translated, m.Index - 1) || trEnds.Contains(m.Index)) && before.TryGetValue((m.Value, trEnds.Contains(m.Index)), out var ws))
                sb.Append(ws);
            sb.Append(m.Value);
            int e = m.Index + m.Length;
            if (e < translated.Length && (IsWordCharAt(translated, e) || trStarts.Contains(e)) && after.TryGetValue((m.Value, trStarts.Contains(e)), out var ws2))
            {
                sb.Append(ws2);
                insertedAfterAt = e;
            }
            pos = e;
        }
        sb.Append(translated, pos, translated.Length - pos);
        return sb.ToString();
    }
}
