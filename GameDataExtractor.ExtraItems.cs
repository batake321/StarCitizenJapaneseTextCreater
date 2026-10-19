using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

// 決め打ちの接頭辞 (VehicleManufacturerPrefixes / ItemPrefixes) と種類 (IsTypedDiscoveryType) では取り込まない品目を、
// item_index と items に足す (［詳細］で開けるように。例 ［BP］の MISC Ore Pod)。足すのは次の 3 つ:
//   1. 種類 (AttachDef.Type) が ExtraItemTypes のもの (採掘レーザー・採掘モジュール・サルベージのモジュール・サルベージヘッド・トラクタービーム・燃料ノズル)
//   2. 採掘の鉱石ポッド (Container で record 名が Cargo_ShipMining_Pod_* / Cargo_GroundVehicleMining_Pod_*。Data.p4k の
//      libs/foundry/records/entities/scitem/ships/utility/mining/miningpods/ の record と同じ。dcb query の filter はファイルの場所では選べないので名前で選ぶ)
//   3. BP のできる品 (blueprints.output_record)・材料 (blueprint_materials.item_record)、契約の raw_json が参照するエンティティ
// どれも、雛形 (record 名に _Template を含む)・壊れた形 (_Collapsed で終わる)・試験用 (Test の語を含む) は除き、名前のキーに訳がある (english の ini・今の訳・japanese の ini の
// どれかで引けて、仮の文字でない) ものだけを入れる (船の扉・表示板・飛行制御のような名前の無い部品は入らない)。
// インデックス構築の最後 (BP の後) と装備データの再抽出の最後 (船の後) に呼ぶ。今までの DB には、起動後の裏の処理 (MainWindow.StartStartupBackfill) が
// NeedsExtraItems のときに一度だけ RebuildExtraItemsAsync で足す (インデックスの作り直しはしない)
public partial class GameDataExtractor
{
    internal static readonly HashSet<string> ExtraItemTypes = new(StringComparer.Ordinal)
    {
        "WeaponMining", "MiningModifier", "SalvageModifier", "SalvageHead", "TractorBeam", "DockingCollar",
    };

    private static readonly string[] OrePodPrefixes = ["Cargo_ShipMining_Pod_", "Cargo_GroundVehicleMining_Pod_"];

    // 追加の品目の raw_json に残す部品 (SlimRawJsonComponents に加えて)。［詳細］の性能 (GameDataQueryService.FillExtraItemCells) が読む
    //   ResourceContainer: 容量・入れられる資源 / SHealthComponentParams: 耐久値 / SEntityPhysicsControllerParams: 質量
    //   SEntityComponentMiningLaserParams: 採掘レーザーの補正 / SCItemWeaponComponentParams: 射程 (採掘レーザー・トラクタービーム)
    //   SItemPortContainerComponentParams: モジュールスロット / EntityComponentAttachableModifierParams: 採掘モジュール・スクレーパーモジュールの補正
    internal static readonly HashSet<string> ExtraItemKeepComponents = new(StringComparer.Ordinal)
    {
        "ResourceContainer", "SHealthComponentParams", "SEntityPhysicsControllerParams", "SEntityComponentMiningLaserParams",
        "SCItemWeaponComponentParams", "SItemPortContainerComponentParams", "EntityComponentAttachableModifierParams",
    };

    // 追加の品目を取り込んだ時刻 (UTC, ISO 8601) の gamedata_meta の記録。取り込みと同じトランザクションで書く (0 件でも書く)。
    // インデックス構築の最初 (RebuiltDataMetaKeys)・装備データの再抽出の最後の取り込みの前・バックアップの取り込み (DatabaseBackupService.ImportIndexDb) で消す
    internal const string ExtraItemsMetaKey = "extra_items_extracted_at";

    // 契約の raw_json の中のエンティティの参照 (".../records/entities/.../<ファイル名>.json")。ファイル名は record 名の短い名前を小文字にしたもの
    private static readonly Regex EntityRefFileRegex = new(@"libs/foundry/records/entities/[^""]*?/([A-Za-z0-9_\-]+)\.json", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 試験用の record (例 Mining_Laser_TEST・Mining_Laser_GRIN_Arbor_S1_Test_Active_1。名前は本物の品目と同じ)。短い名前の "_" で区切った語に Test があるもの
    private static readonly Regex TestRecordRegex = new(@"(^|_)test(_|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> PlaceholderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "<= PLACEHOLDER =>", "<= プレースホルダー =>", "@LOC_PLACEHOLDER", "@LOC_EMPTY", "@LOC_UNINITIALIZED",
    };

    // インデックスを構築した DB で、追加の品目をまだ取り込んでいない (記録が無い) か。起動後の RebuildExtraItemsAsync を走らせるか決めるため
    public bool NeedsExtraItems()
    {
        EnsureDb();
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM item_index) AND NOT EXISTS (SELECT 1 FROM gamedata_meta WHERE key = @k)";
        cmd.Parameters.AddWithValue("@k", ExtraItemsMetaKey);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) != 0;
    }

    // 起動後に裏で一度だけ呼ぶ (MainWindow: NeedsExtraItems のとき)。dcb query が数回なので Game2.dcb は取り出さず Data.p4k から直接読む。
    // 途中で構築が始まったら (IsIndexBuildRunning)、構築が作り直すので書き込まずに 0 を返す。入れた品目の数を返す
    public async Task<int> RebuildExtraItemsAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var p4kPath = FindDataP4k()
            ?? throw new FileNotFoundException("Data.p4k が見つかりません。設定のゲームパスを確認してください。");
        await EnsureStarBreakerAsync();
        EnsureDb();
        Action<string>? relay = progress != null ? msg => progress.Report(msg) : null;
        if (relay != null) StatusChanged += relay;
        try
        {
            return await ExtractExtraItemsAsync(p4kPath, DateTime.UtcNow.ToString("o"), ct, null, skipWriteIfIndexBuildStarts: true);
        }
        finally
        {
            if (relay != null) StatusChanged -= relay;
        }
    }

    // 追加の品目の record の短い名前 (EntityClassDefinition. を除いたもの) を選ぶ。typeMap は record 名 → 種類 (QueryEntityTypesAsync)。
    // item_index にある record (大文字小文字無視)・雛形・壊れた形は除く。refs は BP・契約から参照される短い名前 (大文字小文字無視)
    internal static List<string> SelectExtraItemRecords(Dictionary<string, string> typeMap, HashSet<string> inIndex, IEnumerable<string> refs)
    {
        const string head = "EntityClassDefinition.";
        var refSet = new HashSet<string>(refs, StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var (rec, type) in typeMap)
        {
            if (!rec.StartsWith(head, StringComparison.Ordinal) || type.Length == 0 || type == "UNDEFINED") continue;
            var shortName = rec[head.Length..];
            if (inIndex.Contains(rec)) continue;
            if (shortName.Contains("_Template", StringComparison.OrdinalIgnoreCase) || shortName.EndsWith("_Collapsed", StringComparison.OrdinalIgnoreCase)
                || TestRecordRegex.IsMatch(shortName)) continue;
            var pick = ExtraItemTypes.Contains(type)
                       || (type == "Container" && OrePodPrefixes.Any(p => shortName.StartsWith(p, StringComparison.Ordinal)))
                       || refSet.Contains(shortName);
            if (pick) result.Add(shortName);
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    // BP のできる品・材料と、契約の raw_json が参照するエンティティの短い名前 (表・列が無ければ飛ばす)
    private List<string> ReadReferencedEntityNames()
    {
        const string head = "EntityClassDefinition.";
        var names = new List<string>();
        void ReadColumn(string sql)
        {
            try
            {
                using var cmd = _db!.CreateCommand();
                cmd.CommandText = sql;
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (r.IsDBNull(0)) continue;
                    var v = r.GetString(0).Trim();
                    if (v.StartsWith(head, StringComparison.OrdinalIgnoreCase)) v = v[head.Length..];
                    if (v.Length > 0) names.Add(v);
                }
            }
            catch (SqliteException) { }
        }
        ReadColumn("SELECT DISTINCT output_record FROM blueprints WHERE output_record IS NOT NULL AND output_record <> ''");
        ReadColumn("SELECT DISTINCT item_record FROM blueprint_materials WHERE item_record IS NOT NULL AND item_record <> ''");
        try
        {
            using var cmd = _db!.CreateCommand();
            cmd.CommandText = "SELECT raw_json FROM missions WHERE raw_json LIKE '%records/entities/%'";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (r.IsDBNull(0)) continue;
                foreach (Match m in EntityRefFileRegex.Matches(r.GetString(0))) names.Add(m.Groups[1].Value);
            }
        }
        catch (SqliteException) { }
        return names;
    }

    // 追加の品目を item_index・items・item_ports・item_port_labels に足し、日本語名と名前欄の検索 (item_index_fts) を作り直す。
    // 足した品目の数を返す。終わったら ExtraItemsMetaKey を書く (0 件でも)
    private async Task<int> ExtractExtraItemsAsync(string p4kPath, string now, CancellationToken ct, Dictionary<string, string>? typeMap = null, bool skipWriteIfIndexBuildStarts = false)
    {
        const string head = "EntityClassDefinition.";
        var sw = Stopwatch.StartNew();
        StatusChanged?.Invoke("[品目の追加] 採掘レーザー・採掘モジュール・鉱石ポッド・BP や契約から参照される品目などを探しています...");
        typeMap ??= await QueryEntityTypesAsync(p4kPath, ct);
        if (typeMap.Count == 0)
        {
            StatusChanged?.Invoke("[品目の追加] エンティティの種類の一覧を取得できなかったので取り込みませんでした (次の起動でまた試します)");
            return 0;
        }
        var inIndex = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = _db!.CreateCommand())
        {
            cmd.CommandText = "SELECT record_name FROM item_index";
            using var r = cmd.ExecuteReader();
            while (r.Read()) if (!r.IsDBNull(0)) inIndex.Add(r.GetString(0));
        }
        var candidates = SelectExtraItemRecords(typeMap, inIndex, ReadReferencedEntityNames());
        var filters = BuildRecordFilters(candidates);
        var names = await LoadItemNameDictsAsync("[品目の追加] ", ct);

        // 問い合わせ (読むだけ)。書き込みは下でまとめて 1 つのトランザクションで行う
        var outputs = new List<(string Raw, HashSet<string> Records)>();
        foreach (var (filter, records) in filters)
        {
            ct.ThrowIfCancellationRequested();
            var raw = await RunDcbQueryAsync(p4kPath, "EntityClassDefinition", filter, ct);
            if (!string.IsNullOrEmpty(raw)) outputs.Add((raw, records));
        }

        if (skipWriteIfIndexBuildStarts && IsIndexBuildRunning)
        {
            StatusChanged?.Invoke("[品目の追加] インデックス構築・装備データの再抽出が始まったので、取り出した値は書き込みませんでした");
            return 0;
        }
        ct.ThrowIfCancellationRequested();

        // 親の記録 (例 Mining_Modules。名前は種類の総称「Mining Modules」): 短い名前の後ろに "_" を付けたものが、同じ種類のほかの record の
        // 短い名前の頭になっていて (Mining_Modules_Active_* など)、説明文が空 (@LOC_EMPTY など、訳の無いもの) の record は入れない。
        // データで確かめた範囲では、これに当たるのは Mining_Modules だけ (Cargo_ShipMining_Pod_Golem は _BTGL の頭だが説明文があるので入れる)
        var shortsByType = typeMap.Where(kv => kv.Key.StartsWith(head, StringComparison.Ordinal))
            .GroupBy(kv => kv.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key[head.Length..]).ToList(), StringComparer.Ordinal);
        bool IsParentRecord(string shortName, string itemType) =>
            shortsByType.TryGetValue(itemType, out var shorts)
            && shorts.Any(o => o.Length > shortName.Length + 1 && o.StartsWith(shortName + "_", StringComparison.OrdinalIgnoreCase));
        int count = 0, noName = 0, parents = 0, portRows = 0, labelRows = 0;
        var byType = new SortedDictionary<string, int>(StringComparer.Ordinal);
        using (var tx = _db.BeginTransaction())
        {
            using var idx = _db.CreateCommand();
            idx.Transaction = tx;
            idx.CommandText = "INSERT OR IGNORE INTO item_index(uuid, record_name, name, name_ja, item_type, sub_type, manufacturer, size, extracted_at, name_key) VALUES(@uuid, @rn, @nm, @ja, @it, @st, @mf, @sz, @ea, @nk)";
            var pUuid = idx.Parameters.Add("@uuid", SqliteType.Text);
            var pRn = idx.Parameters.Add("@rn", SqliteType.Text);
            var pNm = idx.Parameters.Add("@nm", SqliteType.Text);
            var pJa = idx.Parameters.Add("@ja", SqliteType.Text);
            var pIt = idx.Parameters.Add("@it", SqliteType.Text);
            var pSt = idx.Parameters.Add("@st", SqliteType.Text);
            var pMf = idx.Parameters.Add("@mf", SqliteType.Text);
            var pSz = idx.Parameters.Add("@sz", SqliteType.Integer);
            var pEa = idx.Parameters.Add("@ea", SqliteType.Text);
            var pNk = idx.Parameters.Add("@nk", SqliteType.Text);
            using var items = _db.CreateCommand();
            items.Transaction = tx;
            items.CommandText = "INSERT OR IGNORE INTO items(record_name,name,item_type,item_sub_type,size,grade,manufacturer,component_type,component_json,raw_json,extracted_at) VALUES(@rn,@nm,@it,@ist,@sz,@gr,@mf,'','',@rj,@ea)";
            var iRn = items.Parameters.Add("@rn", SqliteType.Text);
            var iNm = items.Parameters.Add("@nm", SqliteType.Text);
            var iIt = items.Parameters.Add("@it", SqliteType.Text);
            var iIst = items.Parameters.Add("@ist", SqliteType.Text);
            var iSz = items.Parameters.Add("@sz", SqliteType.Integer);
            var iGr = items.Parameters.Add("@gr", SqliteType.Integer);
            var iMf = items.Parameters.Add("@mf", SqliteType.Text);
            var iRj = items.Parameters.Add("@rj", SqliteType.Text);
            var iEa = items.Parameters.Add("@ea", SqliteType.Text);

            foreach (var (raw, records) in outputs)
            {
                var accepted = new HashSet<string>(StringComparer.Ordinal);
                foreach (var block in SplitJsonBlocks(raw))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(block);
                        var root = doc.RootElement;
                        var recordName = root.TryGetProperty("_RecordName_", out var rn) ? rn.GetString() ?? "" : "";
                        var uuid = root.TryGetProperty("_RecordId_", out var rid) ? rid.GetString() ?? "" : "";
                        if (string.IsNullOrEmpty(recordName) || string.IsNullOrEmpty(uuid) || !records.Contains(recordName)) continue;
                        if (inIndex.Contains(recordName)) continue;
                        if (!root.TryGetProperty("_RecordValue_", out var rv) || !rv.TryGetProperty("Components", out var components) || components.ValueKind != JsonValueKind.Array) continue;
                        string rawName = "", itemType = "", subType = "", manufacturer = "", descKey = "";
                        int size = 0, grade = 0;
                        foreach (var comp in components.EnumerateArray())
                        {
                            if (comp.ValueKind != JsonValueKind.Object) continue;
                            if ((comp.TryGetProperty("_Type_", out var t) ? t.GetString() : null) != "SAttachableComponentParams" || !comp.TryGetProperty("AttachDef", out var ad)) continue;
                            itemType = ad.TryGetProperty("Type", out var it2) ? it2.GetString() ?? "" : "";
                            subType = ad.TryGetProperty("SubType", out var st2) ? st2.GetString() ?? "" : "";
                            manufacturer = ad.TryGetProperty("Manufacturer", out var mf2) ? mf2.GetString() ?? "" : "";
                            size = ad.TryGetProperty("Size", out var sz2) && sz2.ValueKind == JsonValueKind.Number && sz2.TryGetInt32(out var szv) ? szv : 0;
                            grade = ad.TryGetProperty("Grade", out var g2) && g2.ValueKind == JsonValueKind.Number && g2.TryGetInt32(out var gv) ? gv : 0;
                            if (ad.TryGetProperty("Localization", out var loc))
                            {
                                if (loc.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String) rawName = n.GetString() ?? "";
                                if (loc.TryGetProperty("Description", out var dsc) && dsc.ValueKind == JsonValueKind.String) descKey = dsc.GetString() ?? "";
                            }
                            break;
                        }
                        if (string.IsNullOrEmpty(itemType) || itemType == "UNDEFINED") continue;
                        // 名前のキーに訳があるものだけ (仮の文字・訳の無いキーは入れない)
                        var (name, nameJa, nameKey, ok) = names.Resolve(rawName);
                        if (!ok || nameKey == null || PlaceholderNames.Contains(rawName) || PlaceholderNames.Contains(name.Trim())
                            || string.IsNullOrWhiteSpace(name) || name.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase))
                        {
                            noName++;
                            continue;
                        }
                        if (recordName.StartsWith(head, StringComparison.Ordinal) && IsParentRecord(recordName[head.Length..], itemType)
                            && (PlaceholderNames.Contains(descKey) || !names.Resolve(descKey).Resolved))
                        {
                            parents++;
                            continue;
                        }
                        var indexSize = size > 0 ? size : InferSizeFromEntityName(recordName);
                        pUuid.Value = uuid; pRn.Value = recordName; pNm.Value = name;
                        pJa.Value = string.IsNullOrEmpty(nameJa) ? DBNull.Value : nameJa;
                        pIt.Value = itemType; pSt.Value = subType; pMf.Value = manufacturer; pSz.Value = indexSize; pEa.Value = now;
                        pNk.Value = nameKey;
                        if (idx.ExecuteNonQuery() == 0) continue;
                        // items の name は今までどおり "@キー" のまま (ExtractItemsAsync と同じ)。raw_json は性能に使う部品だけを残す
                        iRn.Value = recordName; iNm.Value = rawName; iIt.Value = itemType; iIst.Value = subType;
                        iSz.Value = size; iGr.Value = grade; iMf.Value = manufacturer;
                        iRj.Value = SlimItemRawJson(root, itemType, ExtraItemKeepComponents); iEa.Value = now;
                        items.ExecuteNonQuery();
                        accepted.Add(recordName);
                        inIndex.Add(recordName);
                        byType[itemType] = byType.TryGetValue(itemType, out var c0) ? c0 + 1 : 1;
                        count++;
                    }
                    catch (JsonException) { }
                }
                if (accepted.Count > 0)
                {
                    portRows += InsertItemPortsFromJson(_db, tx, raw, accepted);
                    labelRows += InsertItemPortLabelsFromJson(_db, tx, raw, accepted);
                }
            }
            using (var meta = _db.CreateCommand())
            {
                meta.Transaction = tx;
                meta.CommandText = "INSERT OR REPLACE INTO gamedata_meta(key, value) VALUES(@k, @v)";
                meta.Parameters.AddWithValue("@k", ExtraItemsMetaKey);
                meta.Parameters.AddWithValue("@v", now);
                meta.ExecuteNonQuery();
            }
            tx.Commit();
        }

        if (count > 0)
        {
            // 足した品目の日本語名 (translations.db) と名前欄の検索 (item_index_fts。全件を作り直す)
            var translationDbPath = Path.Combine(Path.GetDirectoryName(DbPath) ?? "", "translations.db");
            if (File.Exists(translationDbPath)) PopulateJapaneseNamesCore(_db, translationDbPath);
            RebuildFts5Index(_db);
        }
        StatusChanged?.Invoke($"[品目の追加] {count} 件を取り込みました ({string.Join(", ", byType.Select(kv => $"{kv.Key} {kv.Value}"))}" +
                              $"{(byType.Count > 0 ? "、" : "")}候補 {candidates.Count} 件のうち名前の訳が無いもの {noName} 件・親の記録 {parents} 件は入れていません。子ポートの定義 {portRows} 行、枠の表示名 {labelRows} 行。{sw.Elapsed.TotalSeconds:F1} 秒)");
        return count;
    }
}
