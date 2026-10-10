using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

public class GameDataQueryService : IDisposable
{
    private readonly SqliteConnection _conn;

    public GameDataQueryService(string gamedataCacheDbPath)
    {
        _conn = new SqliteConnection($"Data Source={gamedataCacheDbPath}");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS knowledge (id INTEGER PRIMARY KEY AUTOINCREMENT, category TEXT NOT NULL DEFAULT 'general', content TEXT NOT NULL, created_at TEXT NOT NULL)";
        cmd.ExecuteNonQuery();
    }

    public bool HasData()
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM missions";
            return (long)(cmd.ExecuteScalar() ?? 0L) > 0;
        }
        catch { return false; }
    }

    // exactRecordName を渡すと、その record_name の船だけを返す (lookup で見つかった item_index の船を引くとき用)
    public string SearchShips(string query, string? exactRecordName = null)
    {
        using var cmd = _conn.CreateCommand();
        // name_en (英語 ini で解決した船名) がある DB ではそれも検索・表示する。列の無い古い DB では従来どおり
        var hasNameEn = ShipsHasNameEn();
        var cols = hasNameEn ? "record_name, name, manufacturer, career, role, crew_size, size, name_en" : "record_name, name, manufacturer, career, role, crew_size, size";
        if (!string.IsNullOrEmpty(exactRecordName))
        {
            cmd.CommandText = $"SELECT {cols} FROM ships WHERE record_name = @q LIMIT 1";
            cmd.Parameters.AddWithValue("@q", exactRecordName);
        }
        else
        {
            cmd.CommandText = hasNameEn
                ? $"SELECT {cols} FROM ships WHERE record_name LIKE @q OR name LIKE @q OR name_en LIKE @q ORDER BY name LIMIT 20"
                : $"SELECT {cols} FROM ships WHERE record_name LIKE @q OR name LIKE @q ORDER BY name LIMIT 20";
            cmd.Parameters.AddWithValue("@q", $"%{query}%");
        }
        using var reader = cmd.ExecuteReader();

        var sb = new StringBuilder();
        int count = 0;
        while (reader.Read())
        {
            var rn = reader.GetString(0);
            var name = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var mfr = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var career = reader.IsDBNull(3) ? "" : reader.GetString(3);
            var role = reader.IsDBNull(4) ? "" : reader.GetString(4);
            var crew = reader.IsDBNull(5) ? 0 : reader.GetInt32(5);
            var size = reader.IsDBNull(6) ? 0 : reader.GetInt32(6);
            var nameEn = hasNameEn && !reader.IsDBNull(7) ? reader.GetString(7) : "";

            sb.Append($"- {rn}");
            if (!string.IsNullOrEmpty(nameEn)) sb.Append($" | 英語名: {nameEn}");
            if (!string.IsNullOrEmpty(name)) sb.Append($" | 名前: {name}");
            if (!string.IsNullOrEmpty(mfr)) sb.Append($" | メーカー: {mfr}");
            if (!string.IsNullOrEmpty(career)) sb.Append($" | 職業: {career}");
            if (!string.IsNullOrEmpty(role)) sb.Append($" | 役割: {role}");
            if (crew > 0) sb.Append($" | 乗員: {crew}");
            if (size > 0) sb.Append($" | サイズ: {size}");
            sb.AppendLine();

            // ハードポイントも取得
            var ports = GetShipPorts(rn);
            if (!string.IsNullOrEmpty(ports)) sb.Append(ports);
            count++;
        }

        return count > 0 ? $"=== ゲームデータ (DB): 船 ===\n{sb}" : "";
    }

    // ships に name_en 列があるか (インデックス構築・マイグレーション前の古い DB には無い)
    private bool ShipsHasNameEn()
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(ships)";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (string.Equals(r.GetString(1), "name_en", StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        return false;
    }

    private string GetShipPorts(string shipRecordName)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT port_name, item_type, size FROM ship_ports WHERE ship_record_name = @srn ORDER BY size DESC, item_type";
        cmd.Parameters.AddWithValue("@srn", shipRecordName);
        using var reader = cmd.ExecuteReader();

        var sb = new StringBuilder();
        while (reader.Read())
        {
            var portName = reader.IsDBNull(0) ? "" : reader.GetString(0);
            var itemType = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var size = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
            sb.AppendLine($"    ポート: {portName} | {itemType} (S{size})");
        }
        return sb.ToString();
    }

    public string SearchItems(string query, string? componentType = null)
    {
        using var cmd = _conn.CreateCommand();
        if (!string.IsNullOrEmpty(componentType))
        {
            cmd.CommandText = "SELECT record_name, name, item_type, size, grade, manufacturer, component_type, component_json FROM items WHERE (record_name LIKE @q OR name LIKE @q) AND component_type = @ct ORDER BY name LIMIT 30";
            cmd.Parameters.AddWithValue("@ct", componentType);
        }
        else
        {
            cmd.CommandText = "SELECT record_name, name, item_type, size, grade, manufacturer, component_type, component_json FROM items WHERE record_name LIKE @q OR name LIKE @q ORDER BY name LIMIT 30";
        }
        cmd.Parameters.AddWithValue("@q", $"%{query}%");
        using var reader = cmd.ExecuteReader();

        var sb = new StringBuilder();
        int count = 0;
        while (reader.Read())
        {
            var name = reader.IsDBNull(1) ? reader.GetString(0) : reader.GetString(1);
            var itemType = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var size = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
            var grade = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);
            var mfr = reader.IsDBNull(5) ? "" : reader.GetString(5);
            var compType = reader.IsDBNull(6) ? "" : reader.GetString(6);
            var compJson = reader.IsDBNull(7) ? "" : reader.GetString(7);

            sb.Append($"- {name} | タイプ: {itemType} | S{size} | Grade {grade}");
            if (!string.IsNullOrEmpty(mfr)) sb.Append($" | {mfr}");

            if (!string.IsNullOrEmpty(compJson))
            {
                var extras = ParseComponentExtras(compType, compJson);
                if (!string.IsNullOrEmpty(extras)) sb.Append($" | {extras}");
            }
            sb.AppendLine();
            count++;
        }

        return count > 0 ? $"=== ゲームデータ (DB): アイテム ===\n{sb}" : "";
    }

    public List<(string uuid, string recordName, string name, string itemType, string subType, string manufacturer)> SearchItemIndex(string query)
    {
        var results = LikeSearch(query);
        if (results.Count > 0) return results;

        var segments = SplitOnParticles(query);
        foreach (var seg in segments)
        {
            if (seg.Length < 2) continue;
            results = LikeSearch(seg);
            if (results.Count > 0) return results;
        }

        var ascii = StripToAlphaNum(query);
        if (ascii.Length >= 2)
            return SearchItemIndexStripped(ascii);

        return results;
    }

    private List<(string uuid, string recordName, string name, string itemType, string subType, string manufacturer)> LikeSearch(string query)
    {
        var results = new List<(string, string, string, string, string, string)>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT uuid, record_name, name, item_type, sub_type, manufacturer FROM item_index WHERE name LIKE @q OR name_ja LIKE @q OR record_name LIKE @q ORDER BY name LIMIT 30";
        cmd.Parameters.AddWithValue("@q", $"%{query}%");
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? "" : reader.GetString(2),
                reader.IsDBNull(3) ? "" : reader.GetString(3),
                reader.IsDBNull(4) ? "" : reader.GetString(4),
                reader.IsDBNull(5) ? "" : reader.GetString(5)
            ));
        }
        return results;
    }

    private static string[] SplitOnParticles(string query)
    {
        return System.Text.RegularExpressions.Regex.Split(query,
                "について|での|とは|では|から|まで|です|ます|って|いくら|[のはがをにでともか？?！!]")
            .Select(s => s.Trim())
            .Where(s => s.Length >= 2)
            .OrderByDescending(s => s.Length)
            .ToArray();
    }


    private List<(string uuid, string recordName, string name, string itemType, string subType, string manufacturer)> SearchItemIndexStripped(string strippedQuery)
    {
        var results = new List<(string, string, string, string, string, string)>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT uuid, record_name, name, item_type, sub_type, manufacturer FROM item_index";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var recordName = reader.GetString(1);
            if (StripToAlphaNum(name).Contains(strippedQuery, StringComparison.OrdinalIgnoreCase)
                || StripToAlphaNum(recordName).Contains(strippedQuery, StringComparison.OrdinalIgnoreCase))
            {
                results.Add((
                    reader.GetString(0), recordName, name,
                    reader.IsDBNull(3) ? "" : reader.GetString(3),
                    reader.IsDBNull(4) ? "" : reader.GetString(4),
                    reader.IsDBNull(5) ? "" : reader.GetString(5)
                ));
                if (results.Count >= 30) break;
            }
        }
        return results;
    }

    public string? GetUuidByName(string name)
    {
        var results = SearchItemIndex(name);
        return results.Count > 0 ? results[0].uuid : null;
    }

    public int GetItemIndexCount()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM item_index";
        try { return (int)(long)(cmd.ExecuteScalar() ?? 0L); }
        catch { return 0; }
    }

    public bool HasFts5()
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='item_index_fts'";
            return (long)(cmd.ExecuteScalar() ?? 0L) > 0;
        }
        catch { return false; }
    }

    public bool HasVectorIndex()
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM item_vectors";
            return (long)(cmd.ExecuteScalar() ?? 0L) > 0;
        }
        catch { return false; }
    }

    public List<(string uuid, string recordName, string name, string itemType, string subType, string manufacturer, double score)> FuzzySearch(string query, int limit = 20)
    {
        var results = new List<(string, string, string, string, string, string, double)>();

        if (HasFts5())
        {
            try
            {
                using var cmd = _conn.CreateCommand();
                var ftsQuery = EscapeFts5Query(query);
                cmd.CommandText = @"SELECT i.uuid, i.record_name, i.name, i.item_type, i.sub_type, i.manufacturer, rank
                    FROM item_index_fts f JOIN item_index i ON f.uuid = i.uuid
                    WHERE item_index_fts MATCH @q
                    ORDER BY rank LIMIT @lim";
                cmd.Parameters.AddWithValue("@q", ftsQuery);
                cmd.Parameters.AddWithValue("@lim", limit);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    results.Add((
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.IsDBNull(2) ? "" : reader.GetString(2),
                        reader.IsDBNull(3) ? "" : reader.GetString(3),
                        reader.IsDBNull(4) ? "" : reader.GetString(4),
                        reader.IsDBNull(5) ? "" : reader.GetString(5),
                        reader.IsDBNull(6) ? 0.0 : reader.GetDouble(6)
                    ));
                }
                if (results.Count > 0) return results;
            }
            catch { }
        }

        var likeResults = SearchItemIndex(query);
        return likeResults.Select(r => (r.uuid, r.recordName, r.name, r.itemType, r.subType, r.manufacturer, 0.0)).ToList();
    }

    // 綴りの揺れ用のあいまい検索 (例 "ironcrad" → "Drake Ironclad")。
    // 検索語全体と、item_index の名前 (name と name_ja) の「全体」または「検索語と同じ語数の連続した語」を編集距離 (Damerau-Levenshtein の OSA 版。
    // 挿入・削除・置換・隣接 2 文字の入れ替えをそれぞれ 1 とする) で比べ、類似度 = 1 - 距離 / 長い方の長さ の最大値が minSimilarity 以上のものを
    // 類似度の高い順に返す。同点は名前全体との類似度 (WholeNameSimilarity。name と name_ja の高い方) の高い順、それも同点は名前順。検索語を断片に分けて照合することはしない (検索語の一部だけが名前に含まれても当たらない)
    public List<(string uuid, string recordName, string name, string itemType, string subType, string manufacturer, double score)> EditDistanceSearch(string query, int limit = 10, double minSimilarity = FuzzyMinSimilarity)
    {
        var q = NormalizeForFuzzy(query);
        if (q.Length == 0) return [];
        var results = new List<((string uuid, string recordName, string name, string itemType, string subType, string manufacturer, double score) item, double whole)>();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT uuid, record_name, name, item_type, sub_type, manufacturer, name_ja FROM item_index WHERE name IS NOT NULL AND name != ''";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(2);
            var nameJa = reader.IsDBNull(6) ? "" : reader.GetString(6);
            var sim = Math.Max(NameSimilarity(q, name, minSimilarity), NameSimilarity(q, nameJa, minSimilarity));
            if (sim < minSimilarity) continue;
            var whole = Math.Max(WholeNameSimilarity(q, name), WholeNameSimilarity(q, nameJa));
            results.Add(((reader.GetString(0), reader.GetString(1), name,
                reader.IsDBNull(3) ? "" : reader.GetString(3),
                reader.IsDBNull(4) ? "" : reader.GetString(4),
                reader.IsDBNull(5) ? "" : reader.GetString(5), sim), whole));
        }
        return results
            .OrderByDescending(r => r.item.score).ThenByDescending(r => r.whole).ThenBy(r => r.item.name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(r => r.item)
            .ToList();
    }

    // 検索語と名前全体 (区切りを空白 1 つにまとめたもの) の編集距離の類似度 = 1 - 距離 / 長い方の長さ。名前が空なら 0
    public static double WholeNameSimilarity(string normalizedQuery, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || normalizedQuery.Length == 0) return 0;
        var whole = NormalizeForFuzzy(name);
        if (whole.Length == 0) return 0;
        return 1.0 - (double)OsaDistance(normalizedQuery, whole) / Math.Max(whole.Length, normalizedQuery.Length);
    }

    // EditDistanceSearch の採用下限 (類似度)。0.8 では 5 文字の語に距離 1 までを拾い、3〜4 文字の語の距離 1 (類似度 0.67〜0.75) は拾わない
    public const double FuzzyMinSimilarity = 0.8;

    // 検索語と名前の類似度 (0〜1)。名前全体と、名前を区切り文字で分けた語のうち検索語と同じ語数の連続した語を候補にし、最も近いものを採る。
    // floor より明らかに届かない候補 (長さの差だけで下回るもの) は計算を省く。名前が空なら 0
    public static double NameSimilarity(string normalizedQuery, string name, double floor = 0)
    {
        if (string.IsNullOrWhiteSpace(name) || normalizedQuery.Length == 0) return 0;
        var whole = NormalizeForFuzzy(name);
        if (whole.Length == 0) return 0;
        var words = whole.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var qWords = normalizedQuery.Split(' ').Length;

        double best = 0;
        void Try(string candidate)
        {
            var maxLen = Math.Max(candidate.Length, normalizedQuery.Length);
            // 長さの差 = 距離の下限。これだけで floor と現在の最良を下回るなら計算しない
            var bound = 1.0 - (double)Math.Abs(candidate.Length - normalizedQuery.Length) / maxLen;
            if (bound < floor || bound <= best) return;
            var sim = 1.0 - (double)OsaDistance(normalizedQuery, candidate) / maxLen;
            if (sim > best) best = sim;
        }

        Try(whole);
        if (words.Length > qWords)
            for (int i = 0; i + qWords <= words.Length; i++)
                Try(string.Join(' ', words, i, qWords));
        return best;
    }

    // 区切り文字 (空白・括弧・ハイフン・スラッシュ・引用符・読点など)
    private static readonly char[] FuzzySeparators =
        " \t()（）[]［］{}「」『』【】<>＜＞\"“”'‘’-‐‑–—―－/／,，、。.:：;；!！?？~～・".ToCharArray();

    // 小文字化し、区切り文字を空白 1 つにまとめる
    public static string NormalizeForFuzzy(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return string.Join(' ', s.ToLowerInvariant().Split(FuzzySeparators, StringSplitOptions.RemoveEmptyEntries));
    }

    // Damerau-Levenshtein の OSA (optimal string alignment) 版の編集距離
    private static int OsaDistance(string a, string b)
    {
        int la = a.Length, lb = b.Length;
        if (la == 0) return lb;
        if (lb == 0) return la;
        var d = new int[la + 1, lb + 1];
        for (int i = 0; i <= la; i++) d[i, 0] = i;
        for (int j = 0; j <= lb; j++) d[0, j] = j;
        for (int i = 1; i <= la; i++)
        {
            for (int j = 1; j <= lb; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                int v = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    v = Math.Min(v, d[i - 2, j - 2] + 1);
                d[i, j] = v;
            }
        }
        return d[la, lb];
    }

    // uuid → name_ja (カタカナ検索の候補の並べ替え用)
    public Dictionary<string, string> GetItemNamesJa(IEnumerable<string> uuids)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT name_ja FROM item_index WHERE uuid = @u";
        var p = cmd.Parameters.Add("@u", SqliteType.Text);
        foreach (var u in uuids.Distinct())
        {
            p.Value = u;
            if (cmd.ExecuteScalar() is string ja && ja.Length > 0) result[u] = ja;
        }
        return result;
    }

    public List<(string uuid, string recordName, string name, string itemType, string subType, string manufacturer, double similarity)> SemanticSearch(float[] queryEmbedding, int limit = 10)
    {
        var results = new List<(string uuid, string recordName, string name, string itemType, string subType, string manufacturer, double similarity)>();
        if (!HasVectorIndex()) return results;

        var candidates = new List<(string uuid, float[] embedding)>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT uuid, embedding FROM item_vectors";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var uuid = reader.GetString(0);
                var blob = (byte[])reader[1];
                candidates.Add((uuid, GameDataExtractor.BytesToFloats(blob)));
            }
        }

        var scored = candidates
            .Select(c => (c.uuid, similarity: CosineSimilarity(queryEmbedding, c.embedding)))
            .OrderByDescending(c => c.similarity)
            .Take(limit)
            .ToList();

        foreach (var (uuid, sim) in scored)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT record_name, name, item_type, sub_type, manufacturer FROM item_index WHERE uuid = @u";
            cmd.Parameters.AddWithValue("@u", uuid);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                results.Add((
                    uuid,
                    reader.IsDBNull(0) ? "" : reader.GetString(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2),
                    reader.IsDBNull(3) ? "" : reader.GetString(3),
                    reader.IsDBNull(4) ? "" : reader.GetString(4),
                    sim
                ));
            }
        }
        return results;
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double dot = 0, magA = 0, magB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        var denom = Math.Sqrt(magA) * Math.Sqrt(magB);
        return denom > 0 ? dot / denom : 0;
    }

    private static string StripToAlphaNum(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            var ch = c;
            if (ch >= '！' && ch <= '～') ch = (char)(ch - 0xFEE0);
            if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
                sb.Append(char.ToUpperInvariant(ch));
        }
        return sb.ToString();
    }

    private static string EscapeFts5Query(string query)
    {
        var escaped = query.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }

    private static string ParseComponentExtras(string compType, string compJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(compJson);
            var el = doc.RootElement;
            var parts = new List<string>();

            switch (compType)
            {
                case "SCItemShieldGeneratorParams":
                    if (el.TryGetProperty("MaxShieldHealth", out var mh)) parts.Add($"最大HP: {mh}");
                    if (el.TryGetProperty("MaxShieldRegen", out var mr)) parts.Add($"再生: {mr}");
                    break;
                case "SCItemQuantumDriveParams":
                    if (el.TryGetProperty("quantumFuelRequirement", out var qf)) parts.Add($"燃料: {qf}");
                    if (el.TryGetProperty("jumpRange", out var jr)) parts.Add($"距離: {jr}");
                    if (el.TryGetProperty("spoolUpTime", out var su)) parts.Add($"スプール: {su}");
                    break;
                case "SCItemWeaponComponentParams":
                    if (el.TryGetProperty("fireRate", out var fr)) parts.Add($"発射速度: {fr}");
                    break;
                case "SCItemPowerPlantParams":
                    if (el.TryGetProperty("PowerOutput", out var po)) parts.Add($"出力: {po}");
                    break;
                case "SCItemCoolerParams":
                    if (el.TryGetProperty("CoolingRate", out var cr)) parts.Add($"冷却: {cr}");
                    break;
            }

            return string.Join(" | ", parts);
        }
        catch { return ""; }
    }

    public string SearchMissions(string? keyword = null, string? missionType = null)
    {
        using var cmd = _conn.CreateCommand();
        var conditions = new List<string>();
        if (!string.IsNullOrEmpty(keyword))
        {
            // ContractGenerator 行は record_name が契約 GUID なので、raw_json の "_Name_" (契約 debugName → 生成器 debugName) でも当てる
            // (CASE で CG 行以外では json_extract を評価しない)
            conditions.Add($"(record_name LIKE @q OR title LIKE @q OR title_hud LIKE @q OR mission_type LIKE @q OR mission_giver LIKE @q OR description LIKE @q OR CASE WHEN record_name LIKE '{ContractRecordPrefix}%' THEN json_extract(raw_json, '$._Name_') END LIKE @q)");
            cmd.Parameters.AddWithValue("@q", $"%{keyword}%");
        }
        if (!string.IsNullOrEmpty(missionType))
        {
            conditions.Add("mission_type LIKE @mt");
            cmd.Parameters.AddWithValue("@mt", $"%{missionType}%");
        }

        var where = conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : "";
        // 13 列目: ContractGenerator 行だけ raw_json (表示名に使う debugName を読む)。他の行は NULL
        cmd.CommandText = $"SELECT record_name, title, title_hud, mission_type, difficulty, mission_giver, location_label, description, reward_min, reward_max, required_reputation, lawfulness_type, CASE WHEN record_name LIKE '{ContractRecordPrefix}%' THEN raw_json END FROM missions {where} ORDER BY reward_max DESC LIMIT 30";
        using var reader = cmd.ExecuteReader();

        var sb = new StringBuilder();
        int count = 0;
        while (reader.Read())
        {
            var rn = reader.GetString(0);
            var title = CleanLocKey(reader.IsDBNull(1) ? "" : reader.GetString(1));
            var titleHud = CleanLocKey(reader.IsDBNull(2) ? "" : reader.GetString(2));
            var mType = reader.IsDBNull(3) ? "" : reader.GetString(3);
            var diff = reader.IsDBNull(4) ? "" : reader.GetString(4);
            var giver = CleanLocKey(reader.IsDBNull(5) ? "" : reader.GetString(5));
            var loc = reader.IsDBNull(6) ? "" : reader.GetString(6);
            var desc = CleanLocKey(reader.IsDBNull(7) ? "" : reader.GetString(7));
            var rMin = reader.IsDBNull(8) ? 0.0 : reader.GetDouble(8);
            var rMax = reader.IsDBNull(9) ? 0.0 : reader.GetDouble(9);
            var rep = reader.IsDBNull(10) ? "" : reader.GetString(10);
            var law = reader.IsDBNull(11) ? "" : reader.GetString(11);

            var displayTitle = IsLocKey(title) ? "" : title;
            string friendlyName;
            // 難易度・場所の推測に使う名前 (ContractGenerator 行は GUID ではなく debugName。抽出側と同じくタイトルキーでは推測しない)
            var inferName = rn;
            if (rn.StartsWith(ContractRecordPrefix))
            {
                // ContractGenerator 行 (record_name は契約 GUID): 表示名はタイトル、解決できなければ 契約 debugName → 生成器 debugName → タイトルキー
                var contractRaw = reader.IsDBNull(12) ? "" : reader.GetString(12);
                var (debugName, nameBase, _) = GameDataExtractor.ReadContractDisplayNames(contractRaw);
                friendlyName = !string.IsNullOrEmpty(displayTitle) ? displayTitle : nameBase;
                displayTitle = "";   // 表示名と同じタイトルを重ねて出さない
                inferName = debugName;
                // 合法性: lawfulness_type 列は空なので raw_json の _Resolved_.illegal から出す
                law = ReadContractLawfulness(contractRaw);
            }
            else
            {
                friendlyName = ExtractFriendlyName(rn);
            }
            sb.Append($"- {friendlyName}");
            if (!string.IsNullOrEmpty(displayTitle)) sb.Append($" | タイトル: {displayTitle}");
            if (!string.IsNullOrEmpty(mType)) sb.Append($" | 種別: {mType}");
            if (!string.IsNullOrEmpty(diff)) sb.Append($" | 難易度: {diff}");
            if (string.IsNullOrEmpty(diff))
            {
                var inferred = InferDifficultyFromName(inferName);
                if (!string.IsNullOrEmpty(inferred)) sb.Append($" | 難易度: {inferred}");
            }
            if (!string.IsNullOrEmpty(giver) && !IsLocKey(giver)) sb.Append($" | 依頼者: {giver}");
            if (rMin > 0 || rMax > 0)
            {
                if (rMin > 0 && rMax > 0 && rMin != rMax) sb.Append($" | 報酬: {rMin:0}-{rMax:0} aUEC");
                else sb.Append($" | 報酬: {Math.Max(rMin, rMax):0} aUEC");
            }
            if (!string.IsNullOrEmpty(rep)) sb.Append($" | 必要評判: {rep}");
            if (!string.IsNullOrEmpty(law)) sb.Append($" | 合法性: {law}");
            if (!string.IsNullOrEmpty(loc)) sb.Append($" | 場所: {loc}");
            var inferredLoc = InferLocationFromName(inferName);
            if (!string.IsNullOrEmpty(inferredLoc) && string.IsNullOrEmpty(loc)) sb.Append($" | 場所: {inferredLoc}");
            sb.AppendLine();

            if (!string.IsNullOrEmpty(desc) && !IsLocKey(desc))
            {
                var truncDesc = desc.Length > 120 ? desc[..120] + "..." : desc;
                sb.AppendLine($"  概要: {truncDesc}");
            }
            count++;
        }

        return count > 0 ? $"=== ゲームデータ (DB): ミッション/契約 ({count}件) ===\n{sb}" : "";
    }

    public string SearchCommodities(string? name = null)
    {
        using var cmd = _conn.CreateCommand();
        if (!string.IsNullOrEmpty(name))
        {
            cmd.CommandText = "SELECT record_name, name, symbol, volatility FROM commodities WHERE record_name LIKE @q OR name LIKE @q ORDER BY name LIMIT 30";
            cmd.Parameters.AddWithValue("@q", $"%{name}%");
        }
        else
        {
            cmd.CommandText = "SELECT record_name, name, symbol, volatility FROM commodities ORDER BY name LIMIT 50";
        }
        using var reader = cmd.ExecuteReader();

        var sb = new StringBuilder();
        int count = 0;
        while (reader.Read())
        {
            var rn = reader.GetString(0);
            var n = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var sym = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var vol = reader.IsDBNull(3) ? "" : reader.GetString(3);

            sb.Append($"- {(!string.IsNullOrEmpty(n) ? n : rn)}");
            if (!string.IsNullOrEmpty(sym)) sb.Append($" ({sym})");
            if (!string.IsNullOrEmpty(vol)) sb.Append($" | 変動性: {vol}");
            sb.AppendLine();
            count++;
        }

        return count > 0 ? $"=== ゲームデータ (DB): コモディティ ({count}件) ===\n{sb}" : "";
    }

    private static string CleanLocKey(string val)
    {
        if (string.IsNullOrEmpty(val)) return val;
        return val.StartsWith('@') ? val[1..] : val;
    }

    private static bool IsLocKey(string val)
    {
        if (string.IsNullOrEmpty(val)) return true;
        if (val.StartsWith('@')) return true;
        if (val.Contains('_') && !val.Contains(' ') && val.Length > 10) return true;
        return false;
    }

    // ContractGenerator 行の record_name の接頭辞 (GameDataExtractor.ContractRecordPrefix と同じ)
    private const string ContractRecordPrefix = GameDataExtractor.ContractRecordPrefix;

    // ContractGenerator 行の raw_json の _Resolved_.illegal (契約 → 生成器 → テンプレート) から 合法 / 非合法 / 不明 を返す
    private static string ReadContractLawfulness(string rawJson)
    {
        if (string.IsNullOrEmpty(rawJson)) return "不明";
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.TryGetProperty("_Resolved_", out var res) && res.ValueKind == JsonValueKind.Object
                && res.TryGetProperty("illegal", out var il))
            {
                if (il.ValueKind == JsonValueKind.False) return "合法";
                if (il.ValueKind == JsonValueKind.True) return "非合法";
            }
        }
        catch { }
        return "不明";
    }

    private static string ExtractFriendlyName(string recordName)
    {
        var name = recordName.Replace("MissionBrokerEntry.", "");
        name = name.Replace("PU_", "");
        name = name.Replace("_", " ");
        return name;
    }

    private static string InferDifficultyFromName(string rn)
    {
        if (rn.Contains("_Intro", StringComparison.OrdinalIgnoreCase)) return "Intro";
        if (rn.Contains("_VeryEasy", StringComparison.OrdinalIgnoreCase)) return "Very Easy";
        if (rn.Contains("_Easy", StringComparison.OrdinalIgnoreCase)) return "Easy";
        if (rn.Contains("_Medium", StringComparison.OrdinalIgnoreCase)) return "Medium";
        if (rn.Contains("_Hard", StringComparison.OrdinalIgnoreCase)) return "Hard";
        if (rn.Contains("_VeryHard", StringComparison.OrdinalIgnoreCase)) return "Very Hard";
        return "";
    }

    private static string InferLocationFromName(string rn)
    {
        if (rn.Contains("Stanton1", StringComparison.OrdinalIgnoreCase)) return "Hurston";
        if (rn.Contains("Stanton2", StringComparison.OrdinalIgnoreCase)) return "Crusader";
        if (rn.Contains("Stanton3", StringComparison.OrdinalIgnoreCase)) return "ArcCorp";
        if (rn.Contains("Stanton4", StringComparison.OrdinalIgnoreCase)) return "microTech";
        if (rn.Contains("Pyro", StringComparison.OrdinalIgnoreCase)) return "Pyro";
        return "";
    }

    // === Knowledge (memory) ===

    /// <summary>Domain keywords for structured knowledge retrieval.</summary>
    private static readonly Dictionary<string, string[]> DomainKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ship"]      = new[] { "ship", "船", "艦", "vehicle", "fighter", "bomber", "hauler", "transport", "mining ship", "gunship",
                                "constellation", "cutlass", "freelancer", "carrack", "reclaimer", "caterpillar", "hammerhead", "retaliator",
                                "hornet", "gladius", "arrow", "sabre", "vanguard", "eclipse", "herald", "hull", "prospector", "mole",
                                "aurora", "mustang", "avenger", "pisces", "spirit", "corsair", "scorpius", "redeemer", "starfarer",
                                "merchantman", "perseus", "polaris", "idris", "javelin", "vulture", "expanse", "starlancer",
                                "raft", "nomad", "100i", "300i", "400i", "600i", "890", "railen", "san'tok", "khartu" },
        ["location"]  = new[] { "location", "station", "ステーション", "都市", "基地", "拠点", "ゲートウェイ", "gateway",
                                "orison", "lorville", "area18", "babbage", "grimhex", "levski", "pyro", "nyx", "stanton",
                                "hurston", "crusader", "arccorp", "microtech", "aberdeen", "daymar", "cellin", "yela",
                                "delamar", "port", "seraphim", "everus", "baijini", "cru-l", "arc-l", "hur-l", "mic-l",
                                "ruin", "checkmate", "bloom", "terra gate", "pyro gateway", "bloom" },
        ["mission"]   = new[] { "mission", "ミッション", "contract", "契約", "bounty", "賞金", "delivery", "配送", "salvage", "サルベージ",
                                "mining", "採掘", "cargo", "patrol", "bunker", "バンカー", "illegal", "smuggle", "rescue", "investigation" },
        ["commodity"] = new[] { "commodity", "商品", "コモディティ", "trade", "貿易", "取引", "cargo", "資源", "鉱石", "ore",
                                "laranite", "quantanium", "agricium", "titanium", "diamond", "corundum", "gold",
                                "stims", "distilled", "medical", "scrap", "waste", "hydrogen", "astatine" },
        ["combat"]    = new[] { "combat", "戦闘", "weapon", "武器", "gun", "銃", "missile", "ミサイル", "shield", "シールド",
                                "armor", "アーマー", "fps", "pvp", "pve", "turret", "タレット", "ammo", "弾薬" },
        ["equipment"] = new[] { "equipment", "装備", "component", "コンポーネント", "cooler", "クーラー", "quantum", "クォンタム",
                                "power plant", "パワープラント", "module", "モジュール", "undersuit", "アンダースーツ",
                                "backpack", "helmet", "ヘルメット", "thruster", "avionics", "radar", "scanner" },
        ["system"]    = new[] { "system", "星系", "stanton", "pyro", "nyx", "sol", "terra", "version", "バージョン", "alpha", "patch",
                                "wipe", "ワイプ", "update", "アップデート", "実装", "roadmap" },
    };

    /// <summary>Extract matching domain tags from user question.</summary>
    public static List<string> ExtractDomains(string question)
    {
        var q = question.ToLowerInvariant();
        var matched = new List<string>();
        foreach (var (domain, keywords) in DomainKeywords)
        {
            if (keywords.Any(kw => q.Contains(kw, StringComparison.OrdinalIgnoreCase)))
                matched.Add(domain);
        }
        return matched;
    }

    /// <summary>Add knowledge with duplicate prevention. Returns existing ID if duplicate found.</summary>
    public (int id, bool isDuplicate) AddKnowledgeSafe(string content, string category = "general")
    {
        // Duplicate check: same category + content similarity (80%+ overlap)
        using var checkCmd = _conn.CreateCommand();
        checkCmd.CommandText = "SELECT id, content FROM knowledge WHERE category = @cat";
        checkCmd.Parameters.AddWithValue("@cat", category);
        using var reader = checkCmd.ExecuteReader();
        while (reader.Read())
        {
            var existingContent = reader.GetString(1);
            if (IsSimilar(content, existingContent))
                return (reader.GetInt32(0), true);
        }
        reader.Close();

        var id = AddKnowledge(content, category);
        return (id, false);
    }

    private static bool IsSimilar(string a, string b)
    {
        if (a == b) return true;
        // Normalize and compare — if one contains the other, or >70% word overlap
        var aNorm = a.Trim().ToLowerInvariant();
        var bNorm = b.Trim().ToLowerInvariant();
        if (aNorm.Contains(bNorm) || bNorm.Contains(aNorm)) return true;

        var aWords = aNorm.Split(' ', '　', '、', '。', ',', '.', '/', '（', '）', '(', ')').Where(w => w.Length > 1).ToHashSet();
        var bWords = bNorm.Split(' ', '　', '、', '。', ',', '.', '/', '（', '）', '(', ')').Where(w => w.Length > 1).ToHashSet();
        if (aWords.Count == 0 || bWords.Count == 0) return false;
        var overlap = aWords.Intersect(bWords).Count();
        var maxLen = Math.Max(aWords.Count, bWords.Count);
        return (double)overlap / maxLen >= 0.7;
    }

    public int AddKnowledge(string content, string category = "general")
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO knowledge(category, content, created_at) VALUES(@cat, @content, @at)";
        cmd.Parameters.AddWithValue("@cat", category);
        cmd.Parameters.AddWithValue("@content", content);
        cmd.Parameters.AddWithValue("@at", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT last_insert_rowid()";
        return (int)(long)cmd.ExecuteScalar()!;
    }

    /// <summary>Search knowledge by keywords (OR match). Returns entries where content matches any keyword.</summary>
    public List<(int id, string category, string content, DateTime createdAt)> SearchKnowledge(IEnumerable<string> keywords)
    {
        var kwList = keywords.Where(k => k.Length >= 2).ToList();
        if (kwList.Count == 0) return GetAllKnowledge();

        var results = new List<(int, string, string, DateTime)>();
        using var cmd = _conn.CreateCommand();
        var conditions = new List<string>();
        for (int i = 0; i < kwList.Count; i++)
        {
            conditions.Add($"content LIKE @kw{i}");
            cmd.Parameters.AddWithValue($"@kw{i}", $"%{kwList[i]}%");
        }
        cmd.CommandText = $"SELECT id, category, content, created_at FROM knowledge WHERE {string.Join(" OR ", conditions)} ORDER BY created_at DESC";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var dt = DateTime.TryParse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed.ToLocalTime() : DateTime.Now;
            results.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2), dt));
        }
        return results;
    }

    /// <summary>Get knowledge filtered by category.</summary>
    public List<(int id, string category, string content, DateTime createdAt)> GetKnowledgeByCategory(string category)
    {
        var results = new List<(int, string, string, DateTime)>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, category, content, created_at FROM knowledge WHERE category = @cat ORDER BY created_at DESC";
        cmd.Parameters.AddWithValue("@cat", category);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var dt = DateTime.TryParse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed.ToLocalTime() : DateTime.Now;
            results.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2), dt));
        }
        return results;
    }

    public int DeleteKnowledge(string query)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM knowledge WHERE content LIKE @q";
        cmd.Parameters.AddWithValue("@q", $"%{query}%");
        return cmd.ExecuteNonQuery();
    }

    public List<(int id, string category, string content, DateTime createdAt)> GetAllKnowledge()
    {
        var results = new List<(int, string, string, DateTime)>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, category, content, created_at FROM knowledge ORDER BY created_at DESC";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var dt = DateTime.TryParse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed.ToLocalTime() : DateTime.Now;
            results.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2), dt));
        }
        return results;
    }

    public int DeleteKnowledgeByIds(IEnumerable<int> ids)
    {
        var idList = string.Join(",", ids);
        if (string.IsNullOrEmpty(idList)) return 0;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM knowledge WHERE id IN ({idList})";
        return cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _conn.Close();
        _conn.Dispose();
    }
}
