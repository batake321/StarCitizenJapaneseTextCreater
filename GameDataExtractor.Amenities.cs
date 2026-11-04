using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

// 星図の場所の施設 (starmap_amenities。［詳細］の場所の施設のアイコンと【基本】の精製所などの有無、［マップ］の星系図の施設のアイコンと施設での検索)。
// 2026-11-02、利用者の指示「詳細の基本の上にアイコンを出して、表に文字で精錬所、補給、外部貨物エレベーターのありなしを出してほしい。
// マップでもアイコン表示と検索にもあるといい。鉱石には要らない」。
// データ: DataCore の StarMapObject の amenities (StarMapAmenityTypeEntry の参照の配列) と、StarMapAmenityTypes.StarMapAmenityTypes の amenityTypes
// (StarMapAmenityTypeEntry: name・displayName (@Maps_Amenities_*)・icon (svg のパス))。どちらも starbreaker dcb query で丸ごと取り出す
// (StarMapObject.amenities のようなプロパティの道筋では、参照の配列は何も出ないため。実測: StarMapObject は約 6 MB・Data.p4k から約 2 秒)。
// インデックス構築 (Step 5) で作る。前の版で構築した DB には表が無いので、起動後に裏で一度だけ取り出す (NeedsStarmapAmenities・RebuildStarmapAmenitiesAsync。
// starmap_names の RebuildStarmapNamesAsync と同じ作り。インデックスの構築はしない)
public partial class GameDataExtractor
{
    // 1 行 = 1 つの場所の 1 つの施設:
    //   record_name: 星図の場所の record 名 (StarMapObject.…。starmap_locations.record_name と同じ形)
    //   ord: その場所の amenities の中の順 (0 から。同じ施設が 2 回出るものは最初の 1 つだけ入れる)
    //   amenity: 施設の name (英語。例 Refinery・Vehicle Services・Commodity Trading - Loading Dock)
    //   display_key: 施設の displayName (訳のキー。例 @Maps_Amenities_Refinery)
    //   icon: 施設のアイコンの Data.p4k の中のパス (Data/ の下。例 UI/Textures/General/Markers/marker_icon_refinery.svg)
    //   type_ord: StarMapAmenityTypes の amenityTypes の中の順 (施設の種類の並び。検索の一覧の順に使う)
    //   type_id: 施設の種類のレコードの GUID (StarMapAmenityTypeEntry の _RecordId_。小文字。［詳細］の【基本】に有無を出す施設を名前でなく GUID で見分けるため)
    internal const string StarmapAmenitiesTable = "starmap_amenities";
    internal const string StarmapAmenitiesDdl = """

        CREATE TABLE IF NOT EXISTS starmap_amenities (
            record_name TEXT NOT NULL,
            ord INTEGER NOT NULL,
            amenity TEXT NOT NULL,
            display_key TEXT NOT NULL,
            icon TEXT NOT NULL,
            type_ord INTEGER NOT NULL,
            type_id TEXT NOT NULL DEFAULT '',
            PRIMARY KEY (record_name, amenity)
        );
        """;

    internal readonly record struct StarmapAmenityRow(string RecordName, int Ord, string Amenity, string DisplayKey, string Icon, int TypeOrd, string TypeId);

    // インデックスを構築した DB で、starmap_amenities の表が無い・空か (前の版で構築した DB・前の版のバックアップを取り込んだ DB)。
    // 起動後の RebuildStarmapAmenitiesAsync を走らせるか決めるため。表は EnsureDb では作らない (無い・空を印にする。
    // 施設のある場所は必ずある (実データで 281 か所) ので、空は「まだ取り出していない・取り出せなかった」)
    public bool NeedsStarmapAmenities()
    {
        EnsureDb();
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM item_index) AND NOT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @t)";
        cmd.Parameters.AddWithValue("@t", StarmapAmenitiesTable);
        if (Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) != 0) return true;
        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM item_index) AND EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @t)";
        if (Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) == 0) return false;
        cmd.CommandText = "SELECT NOT EXISTS (SELECT 1 FROM starmap_amenities)";
        if (Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) != 0) return true;
        // 前の形の表 (施設の種類の GUID の列 type_id が無い。この版の作りかけの間に作った表) も作り直す
        return StarmapAmenitiesLacksTypeId(_db!);
    }

    // starmap_amenities に type_id の列が無いか (表が無ければ false)
    internal static bool StarmapAmenitiesLacksTypeId(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT (SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'starmap_amenities') > 0 " +
                          "AND (SELECT COUNT(*) FROM pragma_table_info('starmap_amenities') WHERE name = 'type_id') = 0";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) != 0;
    }

    // 星図の場所の施設だけを取り出し直す (インデックス構築をせずに starmap_amenities を作る)。入れた行数を返す。
    // 起動後に裏で一度だけ呼ばれる (MainWindow: NeedsStarmapAmenities のとき)。Game2.dcb は取り出さず Data.p4k から直接読む (dcb query 2 回)。
    // 途中で構築が始まったら (IsIndexBuildRunning)、構築が作り直すので書き込まずに 0 を返す
    public async Task<int> RebuildStarmapAmenitiesAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var p4kPath = FindDataP4k()
            ?? throw new FileNotFoundException("Data.p4k が見つかりません。設定のゲームパスを確認してください。");

        EnsureDb();

        Action<string>? relay = progress != null ? msg => progress.Report(msg) : null;
        if (relay != null) StatusChanged += relay;
        try
        {
            var n = await ExtractStarmapAmenitiesAsync(p4kPath, ct, skipWriteIfIndexBuildStarts: true);
            // ［詳細］・［マップ］の ShopTree を作り直させる (施設は BuildShopTree が読む)
            if (n > 0) NotifyShopTablesChanged();
            return n;
        }
        finally
        {
            if (relay != null) StatusChanged -= relay;
        }
    }

    // 施設の種類 (StarMapAmenityTypes) と星図の場所 (StarMapObject) を取り出し、starmap_amenities を入れ替える。
    // 取得できなければ (0 行) 前の行を残して 0 を返す (表が無ければ空の表だけ作る。空なら次の起動でまた試す)。入れた行数を返す
    private async Task<int> ExtractStarmapAmenitiesAsync(string p4kPath, CancellationToken ct, bool skipWriteIfIndexBuildStarts = false)
    {
        StatusChanged?.Invoke("星図の場所の施設 (StarMapObject の amenities) を取り出し中...");
        var typesText = await RunDcbQueryRawAsync(p4kPath, "StarMapAmenityTypes", "*", ct);
        var types = ParseStarmapAmenityTypes(typesText);
        List<StarmapAmenityRow> rows = new();
        if (types.Count > 0)
        {
            var objectsText = await RunDcbQueryRawAsync(p4kPath, "StarMapObject", "*", ct);
            rows = ParseStarmapAmenities(objectsText, types);
        }
        ct.ThrowIfCancellationRequested();
        if (rows.Count == 0)
        {
            try
            {
                using var ddl = _db!.CreateCommand();
                ddl.CommandText = StarmapAmenitiesDdl;
                ddl.ExecuteNonQuery();
            }
            catch (SqliteException) { }
            StatusChanged?.Invoke($"星図の場所の施設を取得できませんでした (施設の種類 {types.Count} 件。前のデータを残します)");
            return 0;
        }
        if (skipWriteIfIndexBuildStarts && IsIndexBuildRunning)
        {
            StatusChanged?.Invoke("星図の場所の施設: インデックス構築・装備データの再抽出が始まったので、取り出した値は書き込みませんでした");
            return 0;
        }
        var dropOld = StarmapAmenitiesLacksTypeId(_db!);
        using (var tx = _db!.BeginTransaction())
        {
            using (var ddl = _db.CreateCommand())
            {
                ddl.Transaction = tx;
                // 前の形の表 (type_id の列が無い) は消して今の形で作る
                ddl.CommandText = (dropOld ? "DROP TABLE starmap_amenities;" : "") + StarmapAmenitiesDdl + "DELETE FROM starmap_amenities;";
                ddl.ExecuteNonQuery();
            }
            using (var ins = _db.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT OR IGNORE INTO starmap_amenities(record_name,ord,amenity,display_key,icon,type_ord,type_id) VALUES(@r,@o,@a,@k,@i,@t,@g)";
                var pR = ins.Parameters.Add("@r", SqliteType.Text);
                var pO = ins.Parameters.Add("@o", SqliteType.Integer);
                var pA = ins.Parameters.Add("@a", SqliteType.Text);
                var pK = ins.Parameters.Add("@k", SqliteType.Text);
                var pI = ins.Parameters.Add("@i", SqliteType.Text);
                var pT = ins.Parameters.Add("@t", SqliteType.Integer);
                var pG = ins.Parameters.Add("@g", SqliteType.Text);
                foreach (var r in rows)
                {
                    pR.Value = r.RecordName; pO.Value = r.Ord; pA.Value = r.Amenity; pK.Value = r.DisplayKey; pI.Value = r.Icon; pT.Value = r.TypeOrd; pG.Value = r.TypeId;
                    ins.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
        StatusChanged?.Invoke($"星図の場所の施設: {rows.Count} 行 (場所 {rows.Select(r => r.RecordName).Distinct().Count()} か所・施設の種類 {types.Count} 件)");
        return rows.Count;
    }

    // dcb query "StarMapAmenityTypes" の出力 → 施設の種類 (StarMapAmenityTypeEntry の _RecordId_ (小文字) → (name, displayName, icon, 並びの番号))
    internal static Dictionary<string, (string Name, string DisplayKey, string Icon, int Ord)> ParseStarmapAmenityTypes(string output)
    {
        var result = new Dictionary<string, (string, string, string, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in SplitJsonBlocks(output))
        {
            try
            {
                using var doc = JsonDocument.Parse(block);
                if (!doc.RootElement.TryGetProperty("_RecordValue_", out var value) || value.ValueKind != JsonValueKind.Object) continue;
                if (!value.TryGetProperty("amenityTypes", out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
                foreach (var e in arr.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    var id = Str(e, "_RecordId_");
                    var name = Str(e, "name");
                    if (id.Length == 0 || name.Length == 0 || result.ContainsKey(id)) continue;
                    result[id] = (name, Str(e, "displayName"), Str(e, "icon"), result.Count);
                }
            }
            catch (JsonException) { }
        }
        return result;

        static string Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";
    }

    // dcb query "StarMapObject" の出力 → 施設の行 (amenities の参照の _RecordId_ を施設の種類で引く。引けない参照・同じ場所の 2 つめ以降の同じ施設は入れない)
    internal static List<StarmapAmenityRow> ParseStarmapAmenities(string output, IReadOnlyDictionary<string, (string Name, string DisplayKey, string Icon, int Ord)> types)
    {
        var rows = new List<StarmapAmenityRow>();
        var seenRecord = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in SplitJsonBlocks(output))
        {
            try
            {
                using var doc = JsonDocument.Parse(block);
                var root = doc.RootElement;
                if (!root.TryGetProperty("_RecordName_", out var rnEl) || rnEl.ValueKind != JsonValueKind.String || rnEl.GetString() is not { Length: > 0 } recordName) continue;
                if (!recordName.StartsWith("StarMapObject.", StringComparison.Ordinal) || !seenRecord.Add(recordName)) continue;
                if (!root.TryGetProperty("_RecordValue_", out var value) || value.ValueKind != JsonValueKind.Object) continue;
                if (!value.TryGetProperty("amenities", out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var ord = 0;
                foreach (var a in arr.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty("_RecordId_", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
                    var typeId = (idEl.GetString() ?? "").Trim().ToLowerInvariant();
                    if (!types.TryGetValue(typeId, out var t) || !seen.Add(t.Name)) continue;
                    rows.Add(new StarmapAmenityRow(recordName, ord++, t.Name, t.DisplayKey, t.Icon, t.Ord, typeId));
                }
            }
            catch (JsonException) { }
        }
        return rows;
    }
}
