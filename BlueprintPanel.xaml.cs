using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

// ［BP］タブ。ブループリントの一覧 (gamedata_cache.db の blueprints / blueprint_materials / blueprint_pools。GameDataExtractor.ExtractBlueprintsAsync が作る) を
// FilterSortGrid に出す。列は できる品 (日本語 (英語))・分類・材料・時間・入手 (初期 / ミッション n 件 / 不明)・在庫 (足りる / 一部 / 不足)。
// 在庫は trade_cache.db の ore_stock (［鉱石］タブ) と照合する: 枠ごとに、その資源 (原石は resource_types.refined_uuid で精錬後に寄せる。1 SCU = 1 SCU) の
// 品質 ≥ min_quality の在庫の合計 ≥ 数量なら満たす。手掘りの鉱物 (CraftingCost_Item の Harvestable_Mineral_1H_<X>) は ResourceType.<X> の '個' の在庫で照合する。
// 資源の名前は［鉱石］タブと同じ出し方 (OreService.LoadResources)、枠名・ミッション名は translations.db (OreService.LoadTranslations)。
// 所持は trade_cache.db の my_blueprints (個人データ。［ゲームログ］の BP受信の行から自動で入るものと、「所持」の欄のボタンで手で入切するもの。HangarService)。
// DB は作業ディレクトリ (WorkDirProvider。MainWindow が渡す)。初めて表示したときに読み、MainWindow が Reload で読み直す。
// タブを開き直したときは在庫 (ore_stock) だけ読み直す (［鉱石］タブで在庫を書き換えた後に合わせるため)
public partial class BlueprintPanel : UserControl
{
    private const string AllLabel = "(すべて)";
    private const string StockEnough = "足りる";
    private const string StockPartial = "一部";
    private const string StockShort = "不足";

    public Func<string>? WorkDirProvider { get; set; }
    // 入手の欄のダブルクリックで選んだミッションを開く (MainWindow がミッションタブに切り替えて詳細を出す)。
    // 引数はその表示名のミッションの record (並んだ順。先頭が代表。先頭がミッションタブに無ければ次を使う)
    public Action<IReadOnlyList<string>>? MissionOpener { get; set; }
    private const int SourceColumn = 4;   // 「入手」の列 (Columns() の順)
    private const int MaterialsColumn = 2;   // 「材料」の列 (Columns() の順)

    private bool _loadedOnce;
    private int _loadVersion;
    private List<BlueprintItem> _items = new();
    // 在庫の照合に使う (読み直しのために持つ): 原石 uuid → 精錬後 uuid、resource_types の record 名 → uuid
    private Dictionary<string, string> _refinedByUuid = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _resourceUuidByRecord = new(StringComparer.OrdinalIgnoreCase);
    // 資源の絞り込みの選択肢 (先頭は「(すべて)」)。チェックの付いたものが選んだ資源
    private List<ResourceChoice> _resourceChoices = new();

    public BlueprintPanel()
    {
        InitializeComponent();
        table.SetColumns([.. Columns(), OwnedColumn()]);
        // できる品 (1 列目) のダブルクリック → ［詳細］タブにその品目を出す
        table.NameDoubleClicked += o => { if (o is BlueprintItem r) DetailPanel.Request(r.OutputRecord, r.Name); };
        // 入手 (5 列目) のダブルクリック → ミッションがあればミッションタブにその詳細を出す (2 つ以上なら名前のメニューから選ぶ)
        table.CellDoubleClicked += OnCellDoubleClicked;
        Loaded += async (_, _) =>
        {
            if (!_loadedOnce)
            {
                _loadedOnce = true;
                await LoadAsync();
            }
            else
                await RefreshStockAsync();
        };
    }

    private string WorkDir()
    {
        var d = WorkDirProvider?.Invoke();
        if (string.IsNullOrEmpty(d)) d = App.Config.WorkingDirectory;
        if (string.IsNullOrEmpty(d)) d = AppDomain.CurrentDomain.BaseDirectory;
        return d;
    }

    // すべて読み直す。MainWindow から、起動の DB 同期・インデックス構築・起動後の裏の取り出し・在庫の変更・バックアップの取り込み・訳の変更の後に呼ぶ。
    // 一度も表示していなければ何もしない (表示したときに読む)。別のスレッドからは UI のスレッドへ回す
    public void Reload()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Reload);
            return;
        }
        if (!_loadedOnce) return;
        _ = LoadAsync();
    }

    // === 読み込み ===

    internal sealed class LoadResult
    {
        public List<BlueprintItem> Items = new();
        public List<ResourceChoice> Resources = new();
        public List<string> Categories = new();
        public Dictionary<string, string> RefinedByUuid = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> ResourceUuidByRecord = new(StringComparer.OrdinalIgnoreCase);
        public bool StockReadable;
        public StockIndex Stock = new();   // 在庫の索引 (読めなければ空)
        public string? Message;   // 表を出せないときの知らせ (null なら出せた)
    }

    private async Task LoadAsync()
    {
        var version = ++_loadVersion;
        txtStatus.Text = "読み込み中...";
        var workDir = WorkDir();
        LoadResult result;
        try { result = await Task.Run(() => LoadData(workDir)); }
        catch (Exception ex) { result = new LoadResult { Message = $"読み込めませんでした: {ex.Message}" }; }
        if (version != _loadVersion) return;

        _items = result.Items;
        _refinedByUuid = result.RefinedByUuid;
        _resourceUuidByRecord = result.ResourceUuidByRecord;
        RefreshResourceCombo(result.Resources);
        RefreshCategoryCombo(result.Categories);
        ApplyFilter();
        txtStatus.Text = result.Message ?? ($"BP {_items.Count} 件" + (result.StockReadable ? "" : " (trade_cache.db が無いので在庫は照合していません)"));
    }

    // 在庫 (ore_stock) だけ読み直して「在庫」の欄を付け直す (タブを開き直したとき)
    private async Task RefreshStockAsync()
    {
        if (_items.Count == 0) return;
        var version = _loadVersion;
        var workDir = WorkDir();
        var refined = _refinedByUuid;
        var byRecord = _resourceUuidByRecord;
        List<OreStock>? stock;
        try { stock = await Task.Run(() => OreService.LoadStockReadOnly(Path.Combine(workDir, "trade_cache.db"))); }
        catch { return; }   // 読めなかったときは前の欄のまま
        if (version != _loadVersion) return;
        var index = BuildStockIndex(stock, refined);
        foreach (var it in _items) it.StockText = StockText(it, index, byRecord, stock != null);
        ApplyFilter();
    }

    // DB を読んで表の行を作る (裏のスレッド)。outputRecord を渡すと、できる品がその record (大文字小文字無視) の BP だけを読む (［詳細］の「ブループリント」の節)。
    // includePersonal が false なら、個人のデータ (在庫 ore_stock・所持 my_blueprints) を読まない (在庫・所持は読めなかったときと同じく空・null。データ API の LAN からの要求)
    internal static LoadResult LoadData(string workDir, string? outputRecord = null, bool includePersonal = true)
    {
        var res = new LoadResult();
        var gdPath = Path.Combine(workDir, "gamedata_cache.db");
        var trPath = Path.Combine(workDir, "translations.db");
        var tcPath = Path.Combine(workDir, "trade_cache.db");
        if (!File.Exists(gdPath))
        {
            res.Message = "インデックスが未構築です (設定タブの［インデックス構築］)";
            return res;
        }

        var bps = new List<BlueprintItem>();
        var byUuid = new Dictionary<string, BlueprintItem>(StringComparer.OrdinalIgnoreCase);
        var poolBps = new Dictionary<string, List<BlueprintItem>>(StringComparer.OrdinalIgnoreCase);   // pool_file → BP
        var itemNames = new Dictionary<string, (string? En, string? Ja)>(StringComparer.OrdinalIgnoreCase);   // item_index の record 名 → (name, name_ja)
        var missionsByPool = new Dictionary<string, List<(string Title, string Record)>>(StringComparer.OrdinalIgnoreCase);   // pool_file → ミッションの (title (訳のキー), record_name)
        var resourceRecords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // uuid → record_name
        using (var game = new SqliteConnection($"Data Source={gdPath};Mode=ReadOnly;Pooling=False"))
        {
            game.Open();
            if (!TableExists(game, "blueprints"))
            {
                res.Message = "BP の表 (blueprints) がありません (起動後の裏の取り出しかインデックス構築の後に入ります)";
                return res;
            }
            using (var cmd = game.CreateCommand())
            {
                cmd.CommandText = "SELECT uuid, record_name, category, output_record, output_name_key, output_name_en, craft_time_sec, process, choose_count, starter FROM blueprints"
                    + (outputRecord != null ? " WHERE output_record = @o COLLATE NOCASE" : "");
                if (outputRecord != null) cmd.Parameters.AddWithValue("@o", outputRecord);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var it = new BlueprintItem
                    {
                        Uuid = Str(r, 0), RecordName = Str(r, 1), Category = Str(r, 2), OutputRecord = Str(r, 3),
                        OutputNameKey = r.IsDBNull(4) ? null : r.GetString(4), OutputNameEn = r.IsDBNull(5) ? null : OreService.EnglishText(r.GetString(5)),
                        CraftTimeSec = r.IsDBNull(6) ? 0 : r.GetInt64(6), Process = Str(r, 7),
                        ChooseCount = r.IsDBNull(8) ? null : r.GetInt32(8), Starter = !r.IsDBNull(9) && r.GetInt64(9) != 0,
                    };
                    if (it.Uuid.Length == 0) continue;
                    bps.Add(it);
                    byUuid.TryAdd(it.Uuid, it);
                }
            }
            if (bps.Count == 0)
            {
                res.Message = "BP がまだありません (起動後の裏の取り出しかインデックス構築の後に入ります)";
                return res;
            }
            // 1 つの品 (outputRecord) のときは、その品の BP の行だけを読む (bp_uuid IN (…))
            string BpFilter(SqliteCommand cmd)
            {
                if (outputRecord == null) return "";
                var ps = bps.Select((b, i) => { cmd.Parameters.AddWithValue($"@b{i}", b.Uuid); return $"@b{i}"; }).ToList();
                return $" WHERE bp_uuid IN ({string.Join(", ", ps)})";
            }
            if (TableExists(game, "blueprint_materials"))
            {
                using var cmd = game.CreateCommand();
                cmd.CommandText = "SELECT bp_uuid, slot_debug_name, slot_name_key, resource_uuid, item_record, quantity, min_quality FROM blueprint_materials" + BpFilter(cmd) + " ORDER BY rowid";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (!byUuid.TryGetValue(Str(r, 0), out var it)) continue;
                    it.Slots.Add(new BlueprintSlot
                    {
                        DebugName = Str(r, 1), NameKey = r.IsDBNull(2) ? null : r.GetString(2),
                        ResourceUuid = Str(r, 3), ItemRecord = Str(r, 4),
                        Quantity = r.IsDBNull(5) ? 0 : r.GetDouble(5), MinQuality = r.IsDBNull(6) ? 0 : r.GetInt32(6),
                    });
                }
            }
            if (TableExists(game, "blueprint_pools"))
            {
                using var cmd = game.CreateCommand();
                cmd.CommandText = "SELECT pool_file, bp_uuid FROM blueprint_pools" + BpFilter(cmd);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var file = Str(r, 0);
                    if (file.Length == 0 || !byUuid.TryGetValue(Str(r, 1), out var it)) continue;
                    if (!poolBps.TryGetValue(file, out var list)) poolBps[file] = list = new List<BlueprintItem>();
                    list.Add(it);
                }
            }
            // 原石 → 精錬後 (refined_uuid 列が無い前の版の DB では空)
            if (TableExists(game, "resource_types"))
            {
                var hasRefined = ColumnExists(game, "resource_types", "refined_uuid");
                using var cmd = game.CreateCommand();
                cmd.CommandText = hasRefined ? "SELECT uuid, record_name, refined_uuid FROM resource_types" : "SELECT uuid, record_name, NULL FROM resource_types";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var uuid = Str(r, 0);
                    var record = Str(r, 1);
                    if (uuid.Length == 0) continue;
                    resourceRecords[uuid] = record;
                    if (record.Length > 0) res.ResourceUuidByRecord.TryAdd(record, uuid);
                    if (!r.IsDBNull(2) && r.GetString(2) is { Length: > 0 } refined) res.RefinedByUuid[uuid] = refined;
                }
            }
            // できる品・材料の品目の名前 (item_index の name / name_ja)
            // 1 つの品のときは、できる品と材料の品目の record だけ (同じ record (大文字小文字無視) は全体を読むときと同じく先の行 (rowid の順))
            var nameRecords = outputRecord == null ? null
                : bps.Select(b => b.OutputRecord).Concat(bps.SelectMany(b => b.Slots).Select(sl => sl.ItemRecord)).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (TableExists(game, "item_index") && (nameRecords == null || nameRecords.Count > 0))
            {
                using var cmd = game.CreateCommand();
                cmd.CommandText = "SELECT record_name, name, name_ja FROM item_index";
                if (nameRecords != null)
                {
                    var ps = nameRecords.Select((x, i) => { cmd.Parameters.AddWithValue($"@r{i}", x); return $"@r{i}"; }).ToList();
                    cmd.CommandText += $" WHERE record_name COLLATE NOCASE IN ({string.Join(", ", ps)}) ORDER BY rowid";
                }
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    AddItemName(itemNames, r);
                }
            }
            // ミッション → 抽選プール (raw_json の BlueprintRewards の blueprintPool のファイル名。MissionModifier_TemporaryBlueprint などの中のものは報酬ではないので数えない)。
            // 1 つの品のときは、その品の BP を含むプールを参照するミッションだけ (LIKE で候補を絞り、正規表現で確かめる)
            var poolFilter = outputRecord == null ? null : poolBps.Keys.Select(f => f.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
            if (TableExists(game, "missions") && (poolFilter == null || poolFilter.Count > 0))
            {
                using var cmd = game.CreateCommand();
                cmd.CommandText = "SELECT title, raw_json, record_name FROM missions WHERE raw_json LIKE '%blueprintPool%'";
                if (poolFilter != null)
                {
                    var ps = poolFilter.Select((f, i) => { cmd.Parameters.AddWithValue($"@p{i}", $"%/{f}.json%"); return $"raw_json LIKE @p{i}"; }).ToList();
                    cmd.CommandText += $" AND ({string.Join(" OR ", ps)})";
                }
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var title = Str(r, 0);
                    var raw = Str(r, 1);
                    var record = Str(r, 2);
                    foreach (var file0 in RewardPoolFiles(raw))
                    {
                        var file = file0.ToLowerInvariant();
                        if (poolFilter != null && !poolFilter.Contains(file)) continue;
                        if (!missionsByPool.TryGetValue(file, out var list)) missionsByPool[file] = list = new List<(string Title, string Record)>();
                        list.Add((title, record));
                    }
                }
            }
        }

        // 資源の名前 (［鉱石］タブと同じ出し方)
        var resources = CachedResources(gdPath, trPath);
        var resourceByUuid = new Dictionary<string, OreResource>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in resources) resourceByUuid.TryAdd(x.Uuid, x);
        string ResourceName(string uuid) =>
            resourceByUuid.TryGetValue(uuid, out var x) ? x.Display
            : resourceRecords.TryGetValue(uuid, out var rec) && rec.Length > 0 ? OreService.ShortRecord(rec)
            : (uuid.Length > 8 ? uuid[..8] : uuid);
        // ［詳細］に資源を出すときの record 名 (resource_types の record 名。無ければ null) と名前 (英語名。無ければ record 名の "ResourceType." を除いたもの)
        string? ResourceRecordOf(string uuid) =>
            resourceByUuid.TryGetValue(uuid, out var x) && x.RecordName.Length > 0 ? x.RecordName
            : resourceRecords.TryGetValue(uuid, out var rec) && rec.Length > 0 ? rec : null;
        string ResourceNameEn(string uuid, string record) =>
            resourceByUuid.TryGetValue(uuid, out var x) && x.NameEn != null ? x.NameEn : OreService.ShortRecord(record);

        // 訳: 枠名・ミッション名・できる品の名前のキー (日本語と英語を今の訳から引く)
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in bps)
        {
            foreach (var s in it.Slots) if (s.NameKey is { Length: > 0 } k) keys.Add(k);
            if (it.OutputNameKey is { Length: > 0 } ok) keys.Add(ok);
        }
        foreach (var list in missionsByPool.Values) foreach (var (t, _) in list) if (t.Length > 0) keys.Add(t);
        var loc = CachedTranslations(gdPath, trPath, keys);

        // ミッションの title → 表示名 (日本語 → 英語 → キー)。ミッションタブの一覧と同じく、強調タグを消し ~mission(…) を〔〕にする (MissionDisplayText.ToPlain)
        string MissionName(string title)
        {
            if (loc.TryGetValue(title, out var t))
                return CleanName(MissionDisplayText.ToPlain(OreService.JapaneseText(t.Ja) ?? OreService.EnglishText(t.En) ?? title));
            return CleanName(title);
        }

        // 行の文字を作る
        var usedResources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in bps)
        {
            it.Name = OutputName(it, loc, itemNames);

            // 材料: 枠ごとに「資源名 数量 単位」(品質の下限があれば「品質 n 以上」)。ツールチップには枠名を添える
            var parts = new List<string>();
            var tips = new List<string>();
            foreach (var s in it.Slots)
            {
                string text;
                if (s.ResourceUuid.Length > 0)
                {
                    usedResources.Add(s.ResourceUuid);
                    s.MaterialName = ResourceName(s.ResourceUuid);
                    text = $"{s.MaterialName} {FormatQuantity(s.Quantity)} {OreService.UnitScu}";
                    if (ResourceRecordOf(s.ResourceUuid) is { } rr) { s.DetailRecord = rr; s.DetailName = ResourceNameEn(s.ResourceUuid, rr); }
                }
                else
                {
                    s.MineralResourceUuid = MineralResourceUuid(s.ItemRecord, res.ResourceUuidByRecord);
                    var name = s.MineralResourceUuid != null ? ResourceName(s.MineralResourceUuid)
                        : itemNames.TryGetValue(s.ItemRecord, out var im) ? (OreService.Label(im.Ja, im.En) ?? OreService.ShortRecord(s.ItemRecord))
                        : OreService.ShortRecord(s.ItemRecord);
                    s.MaterialName = name;
                    text = $"{name} {FormatQuantity(s.Quantity)} {OreService.UnitPiece}";
                    // 手掘りの鉱物は資源に変換したものを［詳細］に出す。資源に変換できない品目 (例 harvestable_trophy_1h_yormandi_eye) は品目のまま出す
                    // (名前は item_index の英語名。無ければ record 名の "EntityClassDefinition." を除いたもの)
                    if (s.MineralResourceUuid != null && ResourceRecordOf(s.MineralResourceUuid) is { } mr) { s.DetailRecord = mr; s.DetailName = ResourceNameEn(s.MineralResourceUuid, mr); }
                    else if (s.ItemRecord.Length > 0)
                    {
                        s.DetailRecord = s.ItemRecord;
                        s.DetailName = itemNames.TryGetValue(s.ItemRecord, out var en) && en.En != null ? en.En : OreService.ShortRecord(s.ItemRecord);
                    }
                }
                if (s.MinQuality > 0) text += $" 品質 {s.MinQuality} 以上";
                parts.Add(text);
                var slotName = s.NameKey is { Length: > 0 } sk && loc.TryGetValue(sk, out var st) ? (OreService.JapaneseText(st.Ja) ?? OreService.EnglishText(st.En) ?? s.DebugName) : s.DebugName;
                s.SlotName = slotName;
                tips.Add(slotName.Length > 0 ? $"{slotName}: {text}" : text);
            }
            var prefix = it.ChooseCount is { } cc ? $"{cc} 枠選択: " : "";
            it.MaterialsText = prefix + string.Join(" / ", parts);
            // 材料の欄の中の各枠の文字の位置 (材料の欄のダブルクリックで押した枠を決める。Slots と同じ並び)
            it.MaterialSpans.Clear();
            var at = prefix.Length;
            foreach (var part in parts) { it.MaterialSpans.Add((at, part.Length)); at += part.Length + " / ".Length; }
            it.MaterialsTooltip = parts.Count == 0 ? null : (it.ChooseCount is { } cc2 ? $"{tips.Count} 枠のうち {cc2} 枠を選ぶ\n" : "") + string.Join("\n", tips);

            it.TimeText = FormatTime(it.CraftTimeSec);
        }

        // 入手: 初期所持 / ミッション (抽選プール経由) の件数とミッション名
        var missionsByBp = new Dictionary<string, List<(string Title, string Record)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, list) in poolBps)
        {
            if (!missionsByPool.TryGetValue(file, out var titles)) continue;
            foreach (var it in list)
            {
                if (!missionsByBp.TryGetValue(it.Uuid, out var acc)) missionsByBp[it.Uuid] = acc = new List<(string Title, string Record)>();
                acc.AddRange(titles);
            }
        }
        foreach (var it in bps)
        {
            var sources = new List<string>();
            if (it.Starter) sources.Add("初期");
            if (missionsByBp.TryGetValue(it.Uuid, out var titles) && titles.Count > 0)
            {
                var names = titles.Select(t => MissionName(t.Title)).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                // 名前ごとのミッションの record (同じ名前の派生は並んだ順。入手の欄のダブルクリックで開く)
                foreach (var n in names)
                    it.Missions.Add((n, titles.Where(t => MissionName(t.Title) == n && t.Record.Length > 0).Select(t => t.Record).Distinct(StringComparer.Ordinal).ToList()));
                // 件数はツールチップに並べる名前の数 (同じ名前の派生ミッションは 1 件)
                sources.Add($"契約 {names.Count} 件");
                it.SourceTooltip = string.Join("\n", names);
            }
            it.SourceText = sources.Count > 0 ? string.Join(" / ", sources) : "不明";
        }

        // 在庫 (ore_stock) との照合。読み込みが例外になったときは在庫なし (照合しない・在庫の欄は空欄) として続け、例外はログに残す
        List<OreStock>? stock;
        try { stock = includePersonal ? OreService.LoadStockReadOnly(tcPath) : null; }
        catch (Exception ex)
        {
            stock = null;
            AppendAppLog(workDir, $"[BP] 在庫 (trade_cache.db の ore_stock) を読めませんでした: {ex.GetType().FullName}: {ex.Message}");
        }
        res.StockReadable = stock != null;
        var stockIndex = BuildStockIndex(stock, res.RefinedByUuid);
        res.Stock = stockIndex;
        foreach (var it in bps) it.StockText = StockText(it, stockIndex, res.ResourceUuidByRecord, stock != null);

        // 所持 (trade_cache.db の my_blueprints)。trade_cache.db が無い・読めないときは不明 (null)。読めないときは例外をログに残す
        HashSet<string>? owned;
        try { owned = includePersonal ? HangarService.LoadMyBlueprintUuidsReadOnly(tcPath) : null; }
        catch (Exception ex)
        {
            owned = null;
            AppendAppLog(workDir, $"[BP] 所持 (trade_cache.db の my_blueprints) を読めませんでした: {ex.GetType().FullName}: {ex.Message}");
        }
        foreach (var it in bps) it.Owned = owned == null ? null : owned.Contains(it.Uuid);

        // 絞り込みの候補: 資源 (BP で使われている精錬後の資源。名前の違う原石は「原石名 → 精錬後名」で精錬後に寄せる)、分類
        var choices = new List<ResourceChoice>();
        foreach (var uuid in usedResources)
            choices.Add(new ResourceChoice { Label = ResourceName(uuid), Uuid = uuid });
        foreach (var x in resources)
        {
            if (!res.RefinedByUuid.TryGetValue(x.Uuid, out var refined) || !usedResources.Contains(refined)) continue;
            var refinedName = ResourceName(refined);
            if (string.Equals(x.Display, refinedName, StringComparison.OrdinalIgnoreCase)) continue;
            choices.Add(new ResourceChoice { Label = $"{x.Display} → {refinedName}", Uuid = refined });
        }
        res.Resources = choices.OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase).ToList();
        res.Categories = bps.Select(b => b.Category).Where(c => c.Length > 0).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
        res.Items = bps;
        return res;
    }

    // missions.raw_json の "blueprintPool":"file://…/xxx.json" のファイル名 (xxx)
    private static readonly Regex PoolFileRegex = new("\"blueprintPool\"\\s*:\\s*\"[^\"]*/([^\"/]+)\\.json\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 手掘りの鉱物の品目 record → resource_types の uuid。Harvestable_Mineral_1H_<X> → ResourceType.<X> (OreService.ResolveMineralResource と同じ変換)、
    // Harvestable_Ore_1H_<X>Ore → ResourceType.Ore_<X> (harvestable_ore_1h_saldyniumore → ResourceType.Ore_Saldynium)。鉱物でなければ・無ければ null
    private static string? MineralResourceUuid(string itemRecord, Dictionary<string, string> resourceUuidByRecord)
    {
        if (itemRecord.Length == 0) return null;
        var shortName = itemRecord.StartsWith("EntityClassDefinition.", StringComparison.Ordinal) ? itemRecord["EntityClassDefinition.".Length..] : itemRecord;
        const string mineral = "Harvestable_Mineral_1H_";
        const string ore = "Harvestable_Ore_1H_";
        string? record = null;
        if (shortName.StartsWith(mineral, StringComparison.OrdinalIgnoreCase))
            record = "ResourceType." + shortName[mineral.Length..];
        else if (shortName.StartsWith(ore, StringComparison.OrdinalIgnoreCase))
        {
            var x = shortName[ore.Length..];
            if (x.EndsWith("Ore", StringComparison.OrdinalIgnoreCase)) x = x[..^3];
            record = "ResourceType.Ore_" + x;
        }
        return record != null && resourceUuidByRecord.TryGetValue(record, out var uuid) ? uuid : null;
    }

    // === 在庫の照合 ===

    // 在庫の索引: SCU の行は資源 (原石は精錬後に寄せる) ごと、'個' の行は資源ごとに (品質, 量) を並べる
    internal sealed class StockIndex
    {
        public Dictionary<string, List<(int? Quality, double Quantity)>> Scu = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<(int? Quality, double Quantity)>> Piece = new(StringComparer.OrdinalIgnoreCase);
    }

    private static StockIndex BuildStockIndex(List<OreStock>? stock, Dictionary<string, string> refinedByUuid)
    {
        var index = new StockIndex();
        if (stock == null) return index;
        foreach (var s in stock)
        {
            if (s.ResourceUuid.Length == 0 || s.Quantity <= 0) continue;
            Dictionary<string, List<(int? Quality, double Quantity)>> dict;
            string key;
            if (string.Equals(s.Unit, OreService.UnitPiece, StringComparison.Ordinal))
            {
                dict = index.Piece;
                key = s.ResourceUuid;
            }
            else
            {
                dict = index.Scu;
                key = refinedByUuid.TryGetValue(s.ResourceUuid, out var refined) ? refined : s.ResourceUuid;
            }
            if (!dict.TryGetValue(key, out var list)) dict[key] = list = new List<(int? Quality, double Quantity)>();
            list.Add((s.Quality, s.Quantity));
        }
        return index;
    }

    // 「足りる」(枠を全部満たす。選ぶ数があればその数)・「一部」・「不足」。在庫を読めなければ ''。品質が不明 (NULL) の在庫は品質 0 として数える
    private static string StockText(BlueprintItem it, StockIndex index, Dictionary<string, string> resourceUuidByRecord, bool stockReadable)
    {
        if (!stockReadable || it.Slots.Count == 0) return "";
        int satisfied = 0;
        foreach (var s in it.Slots)
        {
            if (SlotHave(s, index, resourceUuidByRecord) is not { } have) continue;
            if (have + 1e-9 >= s.Quantity) satisfied++;
        }
        var required = it.ChooseCount is { } cc && cc > 0 && cc < it.Slots.Count ? cc : it.Slots.Count;
        return satisfied >= required ? StockEnough : satisfied > 0 ? StockPartial : StockShort;
    }

    // 枠の手持ち量: その資源 (原石は精錬後に寄せた SCU の行、手掘りの鉱物は '個' の行) の品質 ≥ min_quality の在庫の合計。品質が不明 (NULL) の在庫は品質 0。
    // その資源の在庫の行が無い (照合できない品目も) なら null
    private static double? SlotHave(BlueprintSlot s, StockIndex index, Dictionary<string, string> resourceUuidByRecord)
    {
        List<(int? Quality, double Quantity)>? rows = null;
        if (s.ResourceUuid.Length > 0) index.Scu.TryGetValue(s.ResourceUuid, out rows);
        else
        {
            s.MineralResourceUuid ??= MineralResourceUuid(s.ItemRecord, resourceUuidByRecord);
            if (s.MineralResourceUuid != null) index.Piece.TryGetValue(s.MineralResourceUuid, out rows);
        }
        if (rows == null) return null;
        double have = 0;
        foreach (var (quality, quantity) in rows)
            if ((quality ?? 0) >= s.MinQuality) have += quantity;
        return have;
    }

    // === 絞り込み ===

    private void RefreshResourceCombo(List<ResourceChoice> choices)
    {
        // 読み直しても選んでいた資源は残す (同じラベルと uuid のもの、無ければ同じ uuid のもの)
        var prev = _resourceChoices.Where(c => c.Uuid != null && c.IsSelected).Select(c => (c.Uuid, c.Label)).ToList();
        var list = new List<ResourceChoice> { new() { Label = AllLabel, Uuid = null } };
        list.AddRange(choices);
        foreach (var (uuid, label) in prev)
        {
            var keep = list.FirstOrDefault(c => c.Uuid != null && string.Equals(c.Uuid, uuid, StringComparison.OrdinalIgnoreCase) && c.Label == label)
                ?? list.FirstOrDefault(c => c.Uuid != null && string.Equals(c.Uuid, uuid, StringComparison.OrdinalIgnoreCase));
            if (keep != null) keep.IsSelected = true;
        }
        _resourceChoices = list;
        lstResource.ItemsSource = list;
        UpdateResourceSelection();
    }

    // 資源のチェックを押したとき。「(すべて)」なら選択を全部外す。すぐ表を絞り直す
    private void ResourceCheck_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ResourceChoice c) return;
        if (c.Uuid == null)
            foreach (var x in _resourceChoices) x.IsSelected = false;
        UpdateResourceSelection();
        ApplyFilter();
    }

    // 「(すべて)」のチェック (何も選んでいなければ付ける) と、閉じているときの表示 (選んだ資源の名前を「、」でつなぐ。無ければ「(すべて)」) を合わせる
    private void UpdateResourceSelection()
    {
        var selected = _resourceChoices.Where(c => c.Uuid != null && c.IsSelected).ToList();
        if (_resourceChoices.Count > 0) _resourceChoices[0].IsSelected = selected.Count == 0;
        txtResource.Text = selected.Count == 0 ? AllLabel : string.Join("、", selected.Select(c => c.Label));
    }

    private void RefreshCategoryCombo(List<string> categories)
    {
        var prev = cmbCategory.SelectedItem as string;
        var list = new List<string> { AllLabel };
        list.AddRange(categories);
        cmbCategory.ItemsSource = list;
        cmbCategory.SelectedItem = prev != null && list.Contains(prev) ? prev : AllLabel;
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (table == null) return;
        IEnumerable<BlueprintItem> items = _items;
        var uuids = _resourceChoices.Where(c => c.IsSelected && c.Uuid is { Length: > 0 }).Select(c => c.Uuid!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        items = FilterByResources(items, uuids);
        if (cmbCategory?.SelectedItem is string cat && cat != AllLabel)
            items = items.Where(it => string.Equals(it.Category, cat, StringComparison.Ordinal));
        if (chkCraftable?.IsChecked == true)
            items = items.Where(it => it.StockText == StockEnough);
        if (chkOwned?.IsChecked == true)
            items = items.Where(it => it.Owned == true);
        table.SetItems(items.ToList());
    }

    // 選んだ資源 (精錬後の uuid。原石と精錬後を両方選んでも同じ uuid で 1 つの条件) を全部材料に使う BP だけにする。何も選んでいなければそのまま
    internal static IEnumerable<BlueprintItem> FilterByResources(IEnumerable<BlueprintItem> items, IReadOnlyCollection<string> resourceUuids)
    {
        if (resourceUuids.Count == 0) return items;
        return items.Where(it => resourceUuids.All(uuid => it.Slots.Any(s => string.Equals(s.ResourceUuid, uuid, StringComparison.OrdinalIgnoreCase))));
    }

    // 入手の欄のダブルクリック。ミッションが 1 つならそのまま開き、2 つ以上なら表示名 (ツールチップと同じ並び) のメニューを出して選んだものを開く。
    // 「初期」だけ・「不明」 (ミッションが無い) なら何もしない
    private void OnCellDoubleClicked(int column, object source, int charIndex)
    {
        if (column == MaterialsColumn) { OpenMaterialDetail(source, charIndex); return; }
        if (column != SourceColumn || source is not BlueprintItem it || MissionOpener == null) return;
        var missions = it.Missions.Where(m => m.Records.Count > 0).ToList();
        if (missions.Count == 0) return;
        if (missions.Count == 1) { MissionOpener(missions[0].Records); return; }
        var menu = new ContextMenu { PlacementTarget = table, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        foreach (var (name, records) in missions)
        {
            // 見出しは TextBlock にする (文字のままだと「_」がアクセスキーになる)
            var item = new MenuItem { Header = new TextBlock { Text = name } };
            item.Click += (_, _) => MissionOpener?.Invoke(records);
            menu.Items.Add(item);
        }
        // ダブルクリックのボタンを離す前にメニューを出すと、離したときに下の項目が押されることがあるため、離してから出す。
        // 離すのはウィンドウ (どこで離しても) で 1 回だけ受け、1 秒たっても離さなければ (ウィンドウの外で離したときも) 待ちを外してメニューは出さない
        var window = Window.GetWindow(this);
        if (Mouse.LeftButton == MouseButtonState.Pressed && window != null)
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            MouseButtonEventHandler? up = null;
            up = (_, _) =>
            {
                timer.Stop();
                window.PreviewMouseLeftButtonUp -= up;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => menu.IsOpen = true);
            };
            timer.Tick += (_, _) => { timer.Stop(); window.PreviewMouseLeftButtonUp -= up; };
            window.PreviewMouseLeftButtonUp += up;
            timer.Start();
        }
        else menu.IsOpen = true;
    }

    // 材料の欄のダブルクリック: その BP の材料を全部 (枠の順。［詳細］に出せる材料 = DetailRecord のあるもの) ［詳細］タブのリストに並べ、
    // 押した位置の文字の番号 (charIndex) にある枠の材料を選んだ状態で詳細を出す (DetailPanel.RequestList)。資源 (手掘りの鉱物を変換した資源を含む) は資源、
    // 資源に変換できない品目は品目として並べる。押した位置が材料に当たらない (枠の間の " / " の上・「n 枠選択: 」の上・位置が分からない)・
    // 押した材料を［詳細］に出せないときは先頭の材料を選ぶ。出せる材料が 1 つも無ければ何もしない
    private static void OpenMaterialDetail(object source, int charIndex)
    {
        if (source is not BlueprintItem it) return;
        var items = new List<(string Record, string Name, bool IsResource)>();
        var select = 0;
        for (int i = 0; i < it.Slots.Count; i++)
        {
            var s = it.Slots[i];
            if (s.DetailRecord == null) continue;
            if (charIndex >= 0 && i < it.MaterialSpans.Count)
            {
                var (start, length) = it.MaterialSpans[i];
                if (charIndex >= start && charIndex <= start + length) select = items.Count;
            }
            items.Add((s.DetailRecord, s.DetailName ?? s.MaterialName, s.DetailRecord.StartsWith("ResourceType.", StringComparison.OrdinalIgnoreCase)));
        }
        if (items.Count > 0) DetailPanel.RequestList(items, select);
    }

    // === 表の列 ===

    private static List<GridColumnSpec> Columns() =>
    [
        new() { Header = "できる品", Filterable = false, Text = o => ((BlueprintItem)o).Name, Key = o => TextKey(((BlueprintItem)o).Name) },
        new() { Header = "分類", Text = o => ((BlueprintItem)o).Category, Key = o => TextKey(((BlueprintItem)o).Category) },
        new() { Header = "材料", Filterable = false, Text = o => ((BlueprintItem)o).MaterialsText, Key = o => TextKey(((BlueprintItem)o).MaterialsText), Tooltip = o => ((BlueprintItem)o).MaterialsTooltip },
        new() { Header = "時間", Numeric = true, Filterable = false, Text = o => ((BlueprintItem)o).TimeText, Key = o => ((BlueprintItem)o).CraftTimeSec },
        new() { Header = "入手", Text = o => ((BlueprintItem)o).SourceText, Key = o => TextKey(((BlueprintItem)o).SourceText), Tooltip = o => ((BlueprintItem)o).SourceTooltip },
        new() { Header = "在庫", Text = o => ((BlueprintItem)o).StockText, Key = o => TextKey(((BlueprintItem)o).StockText) },
        // 所持: 「所持」/「未所持」のボタン。押すと入切する (ToggleOwned。インスタンスのメソッドなので Columns() の外で付ける)
    ];

    private const string OwnedText = "所持";
    private const string NotOwnedText = "未所持";

    // 所持の列 (ボタン)。Columns() の後ろに足す
    private GridColumnSpec OwnedColumn() => new()
    {
        Header = "所持",
        Text = o => ((BlueprintItem)o).Owned == true ? OwnedText : NotOwnedText,
        Key = o => ((BlueprintItem)o).Owned == true ? OwnedText : NotOwnedText,
        Button = _ => true,
        ButtonClick = o => ToggleOwned((BlueprintItem)o),
    };

    // 所持の欄のボタン: 所持なら外し (行を消して墓石を残す)、未所持なら入れる (source 'manual')。trade_cache.db に書いてから表を絞り直す
    private void ToggleOwned(BlueprintItem it)
    {
        var want = it.Owned != true;
        try
        {
            var hangar = new HangarService();
            hangar.SetCacheDir(WorkDir());
            hangar.SetBlueprintOwned(it.Uuid, it.OutputRecord, it.Name, want);
        }
        catch (Exception ex)
        {
            txtStatus.Text = $"所持を{(want ? "入れ" : "外せ")}ませんでした: {ex.Message}";
            AppendAppLog(WorkDir(), $"[BP] 所持を{(want ? "入れ" : "外せ")}ませんでした ({it.Name}): {ex.GetType().FullName}: {ex.Message}");
            return;
        }
        it.Owned = want;
        ApplyFilter();
    }

    private static IComparable? TextKey(string s) => s.Length > 0 ? s : null;

    // 時間: 1 時間以上は「h 時間 m 分」、それ未満は「m 分 s 秒」
    internal static string FormatTime(long seconds)
    {
        if (seconds < 0) seconds = 0;
        var h = seconds / 3600;
        var m = seconds % 3600 / 60;
        var s = seconds % 60;
        return h > 0 ? $"{h} 時間 {m} 分" : $"{m} 分 {s} 秒";
    }

    internal static string FormatQuantity(double q) => q.ToString("0.##", CultureInfo.InvariantCulture);

    // === ［詳細］・ミッションの詳細と共有する読み方 ===

    // できる品の名前: 日本語 (英語)。日本語は訳 (name_key) → item_index.name_ja、英語は訳の english → blueprints.output_name_en → item_index.name。
    // どちらも無ければ record 名 ("EntityClassDefinition." を除く)
    internal static string OutputName(BlueprintItem it, Dictionary<string, (string? En, string? Ja)> loc, Dictionary<string, (string? En, string? Ja)> itemNames)
    {
        string? ja = null, en = null;
        if (it.OutputNameKey is { Length: > 0 } nk && loc.TryGetValue(nk, out var tr))
        {
            ja = OreService.JapaneseText(tr.Ja);
            en = OreService.EnglishText(tr.En);
        }
        itemNames.TryGetValue(it.OutputRecord, out var inm);
        ja ??= inm.Ja;
        en ??= it.OutputNameEn ?? inm.En;
        return CleanName(OreService.Label(ja, en) ?? (it.OutputRecord.Length > 0 ? OreService.ShortRecord(it.OutputRecord) : it.RecordName));
    }

    // item_index の 1 行 (record_name, name, name_ja) を名前の表に足す (同じ record は先の行を残す)
    private static void AddItemName(Dictionary<string, (string? En, string? Ja)> itemNames, SqliteDataReader r)
    {
        var record = Str(r, 0);
        if (record.Length == 0 || itemNames.ContainsKey(record)) return;
        itemNames[record] = (r.IsDBNull(1) ? null : OreService.EnglishText(r.GetString(1)), r.IsDBNull(2) ? null : OreService.JapaneseText(r.GetString(2)));
    }

    // ［詳細］の「ブループリント」の節: できる品が outputRecord の BP (表の行と同じ作り)・在庫の欄・枠ごとの手持ち量 (SlotHave。在庫を読めなければ Haves は null)。
    // BP の表が無い・その品の BP が無ければ空。includePersonal が false なら在庫・所持を読まない (LoadData)
    internal static List<(BlueprintItem Item, string StockText, List<double?>? Haves)> LoadForOutput(string workDir, string outputRecord, bool includePersonal = true)
    {
        var list = new List<(BlueprintItem Item, string StockText, List<double?>? Haves)>();
        if (string.IsNullOrWhiteSpace(outputRecord)) return list;
        var res = LoadData(workDir, outputRecord.Trim(), includePersonal);
        if (res.Items.Count == 0) return list;
        foreach (var it in res.Items)
            list.Add((it, it.StockText, res.StockReadable ? it.Slots.Select(s => SlotHave(s, res.Stock, res.ResourceUuidByRecord)).ToList() : null));
        return list;
    }

    // ミッション詳細の「報酬の BP」の節: 抽選プール (blueprint_pools.pool_file。小文字で照合) ごとの BP のできる品の名前 (OutputName と同じ出し方。名前の順、同じ名前は 1 つ)。
    // BP の表が無ければ空。プールに BP が無ければそのプールは空の一覧
    internal static Dictionary<string, List<string>> LoadPoolBlueprintNames(string workDir, IReadOnlyCollection<string> poolFiles)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var gdPath = Path.Combine(workDir, "gamedata_cache.db");
        if (poolFiles.Count == 0 || !File.Exists(gdPath)) return result;
        var bpsByPool = new Dictionary<string, List<BlueprintItem>>(StringComparer.OrdinalIgnoreCase);
        var itemNames = new Dictionary<string, (string? En, string? Ja)>(StringComparer.OrdinalIgnoreCase);
        var files = poolFiles.Select(f => f.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
        using (var game = new SqliteConnection($"Data Source={gdPath};Mode=ReadOnly;Pooling=False"))
        {
            game.Open();
            if (!TableExists(game, "blueprints") || !TableExists(game, "blueprint_pools")) return result;
            using (var cmd = game.CreateCommand())
            {
                var ps = files.Select((f, i) => { cmd.Parameters.AddWithValue($"@f{i}", f); return $"@f{i}"; }).ToList();
                cmd.CommandText = "SELECT p.pool_file, b.uuid, b.record_name, b.output_record, b.output_name_key, b.output_name_en FROM blueprint_pools p JOIN blueprints b ON b.uuid = p.bp_uuid"
                    + $" WHERE lower(p.pool_file) IN ({string.Join(", ", ps)})";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var it = new BlueprintItem
                    {
                        Uuid = Str(r, 1), RecordName = Str(r, 2), OutputRecord = Str(r, 3),
                        OutputNameKey = r.IsDBNull(4) ? null : r.GetString(4), OutputNameEn = r.IsDBNull(5) ? null : OreService.EnglishText(r.GetString(5)),
                    };
                    if (it.Uuid.Length == 0) continue;
                    var file = Str(r, 0).ToLowerInvariant();
                    if (!bpsByPool.TryGetValue(file, out var l)) bpsByPool[file] = l = new List<BlueprintItem>();
                    l.Add(it);
                }
            }
            var records = bpsByPool.Values.SelectMany(l => l).Select(it => it.OutputRecord).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (records.Count > 0 && TableExists(game, "item_index"))
            {
                using var cmd = game.CreateCommand();
                var ps = records.Select((x, i) => { cmd.Parameters.AddWithValue($"@r{i}", x); return $"@r{i}"; }).ToList();
                // 表と同じく、同じ record (大文字小文字無視) は先の行 (rowid の順) を使う
                cmd.CommandText = $"SELECT record_name, name, name_ja FROM item_index WHERE record_name COLLATE NOCASE IN ({string.Join(", ", ps)}) ORDER BY rowid";
                using var r = cmd.ExecuteReader();
                while (r.Read()) AddItemName(itemNames, r);
            }
        }
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in bpsByPool.Values) foreach (var it in l) if (it.OutputNameKey is { Length: > 0 } k) keys.Add(k);
        var loc = CachedTranslations(gdPath, Path.Combine(workDir, "translations.db"), keys);
        foreach (var f in files)
            result[f] = bpsByPool.TryGetValue(f, out var l)
                ? l.Select(it => CleanName(OutputName(it, loc, itemNames))).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();
        return result;
    }

    // missions.raw_json の BlueprintRewards の 1 件 ({"_Type_":"BlueprintRewards", ..., "chance":…, "blueprintPool":"…"})
    internal static readonly Regex RewardObjectRegex = new("\\{\\s*\"_Type_\"\\s*:\\s*\"BlueprintRewards\"[^{}]*\\}", RegexOptions.Compiled);

    // raw_json の BlueprintRewards の中の blueprintPool のファイル名 (出てきた順。MissionModifier_TemporaryBlueprint などの中のものは含めない)
    internal static IEnumerable<string> RewardPoolFiles(string rawJson)
    {
        foreach (Match obj in RewardObjectRegex.Matches(rawJson ?? ""))
            foreach (Match m in PoolFileRegex.Matches(obj.Value))
                yield return m.Groups[1].Value;
    }

    // ミッションの raw_json の blueprintPool のファイル名の正規表現
    internal static Regex BlueprintPoolFileRegex => PoolFileRegex;

    // 表示名の中の文字列「\n」(バックスラッシュと n の 2 文字) を空白 1 つにして前後の空白を除く
    // 「\n」(2 文字) を含む名前だけ、空白にしたあとの重なった空白と閉じかっこの前の空白を詰める (ほかの名前は前後の空白を除くだけ)
    internal static string CleanName(string s)
    {
        if (!s.Contains("\\n", StringComparison.Ordinal)) return s.Trim();
        var t = System.Text.RegularExpressions.Regex.Replace(s.Replace("\\n", " "), " {2,}", " ");
        return t.Replace(" )", ")").Replace(" ）", "）").Trim();
    }

    // 作業ディレクトリの app_log.txt に 1 行追記する (MainWindow.Log と同じファイル・同じ行の形。DetailPanel.AppendAppLog と同じ書き方)
    private static void AppendAppLog(string workDir, string msg)
    {
        App.AppendStartupLog(workDir, msg);   // ほかの書き込みと重ならないよう App のロックを通す
    }

    // === ［ゲームログ］の BP受信の品名 → BP ===

    // 通知の品名 name と、できる品の名前が一致する BP (blueprints の rowid の順。先頭が代表)。無ければ null。
    // できる品の名前は、output_name_key の訳 (translations の japanese / english。OutputName と同じく JapaneseText / EnglishText で見る) と output_name_en。
    // 大文字小文字・前後の空白・全角と半角の空白の違いは無視する (NormalizeOutputName)。名前に文字列「\n」(2 文字) があるものは CleanName で空白にした形でも照合する。
    // 索引は gamedata_cache.db と translations.db が変わっていなければ前のものを使う。DB を読めなければ例外
    internal static IReadOnlyList<GameLogBlueprint>? BlueprintsByOutputName(string gdPath, string trPath, string name)
    {
        var key = NormalizeOutputName(name ?? "");
        if (key.Length == 0) return null;
        var index = OutputNameIndex(gdPath, trPath);
        return index.TryGetValue(key, out var list) && list.Count > 0 ? list : null;
    }

    // 照合に使う形: 全角の空白・ノーブレークスペースを半角の空白にし、前後の空白を除く (大文字小文字は索引の比較で無視する)
    internal static string NormalizeOutputName(string s) => s.Replace('\u3000', ' ').Replace('\u00A0', ' ').Trim();

    private static (string Stamp, Dictionary<string, List<GameLogBlueprint>> Index)? _outputNameIndexCache;

    private static Dictionary<string, List<GameLogBlueprint>> OutputNameIndex(string gdPath, string trPath)
    {
        var stamp = FileStamp(gdPath) + "\n" + FileStamp(trPath);
        lock (CacheLock)
        {
            if (_outputNameIndexCache is { } c && c.Stamp == stamp) return c.Index;
        }
        var index = new Dictionary<string, List<GameLogBlueprint>>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(gdPath))
        {
            var bps = new List<(string Uuid, string Record, string? NameKey, string? NameEn)>();
            using (var game = new SqliteConnection($"Data Source={gdPath};Mode=ReadOnly;Pooling=False"))
            {
                game.Open();
                if (TableExists(game, "blueprints"))
                {
                    using var cmd = game.CreateCommand();
                    cmd.CommandText = "SELECT uuid, output_record, output_name_key, output_name_en FROM blueprints ORDER BY rowid";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        var uuid = Str(r, 0);
                        if (uuid.Length == 0) continue;
                        bps.Add((uuid, Str(r, 1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
                    }
                }
            }
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in bps) if (b.NameKey is { Length: > 0 } k) keys.Add(k);
            var loc = CachedTranslations(gdPath, trPath, keys);
            foreach (var b in bps)
            {
                var names = new List<string?>();
                if (b.NameKey is { Length: > 0 } nk && loc.TryGetValue(nk, out var tr))
                {
                    names.Add(OreService.JapaneseText(tr.Ja));
                    names.Add(OreService.EnglishText(tr.En));
                }
                names.Add(b.NameEn != null ? OreService.EnglishText(b.NameEn) : null);
                foreach (var n in names)
                {
                    if (n == null) continue;
                    foreach (var form in new[] { n, CleanName(n) })
                    {
                        var k = NormalizeOutputName(form);
                        if (k.Length == 0) continue;
                        if (!index.TryGetValue(k, out var list)) index[k] = list = new List<GameLogBlueprint>();
                        if (!list.Any(x => string.Equals(x.Uuid, b.Uuid, StringComparison.OrdinalIgnoreCase)))
                            list.Add(new GameLogBlueprint { Uuid = b.Uuid, OutputRecord = b.Record });
                    }
                }
            }
        }
        lock (CacheLock) _outputNameIndexCache = (stamp, index);
        return index;
    }

    // === OreService.LoadResources / LoadTranslations の結果の使い回し ===
    // どちらも translations.db を全行読むため (1 回 80〜100 ms ほど)、DB ファイルのパス・最終更新時刻・長さが同じあいだは前の結果を使う。
    // 結果は読むだけで書き換えない (複数のスレッドから同じものを使う)

    private static readonly object CacheLock = new();
    private static (string Stamp, List<OreResource> Resources)? _resourcesCache;
    private static (string Stamp, HashSet<string> Keys, Dictionary<string, (string? En, string? Ja)> Loc)? _translationsCache;

    // DB ファイルの鍵 (パス・最終更新時刻・長さ。WAL の -wal も見る: SqliteWal.FileStamp)。無ければ「無し」
    private static string FileStamp(string path) => SqliteWal.FileStamp(path);

    // OreService.LoadResources と同じ結果 (gamedata_cache.db と translations.db が変わっていなければ前の結果)
    private static List<OreResource> CachedResources(string gdPath, string trPath)
    {
        var stamp = FileStamp(gdPath) + "\n" + FileStamp(trPath);
        lock (CacheLock)
        {
            if (_resourcesCache is { } c && c.Stamp == stamp) return c.Resources;
            var list = OreService.LoadResources(gdPath, trPath);
            _resourcesCache = (stamp, list);
            return list;
        }
    }

    // OreService.LoadTranslations(trPath, keys) と同じ値を keys について返す (返す表には keys 以外のキーも入っていることがある。引くのは TryGetValue だけにする)。
    // 覚えておくキーは、BP の表で使いうるキー (できる品の名前・枠名・blueprintPool を含むミッションの title) と今まで頼まれたキーの和。
    // LoadTranslations はキーごとに独立に値を決めるが、大文字小文字だけ違う別のキーが和に入っていると値が変わりうるので、そのときは使い回さずにそのまま読む
    private static Dictionary<string, (string? En, string? Ja)> CachedTranslations(string gdPath, string trPath, HashSet<string> keys)
    {
        var stamp = FileStamp(gdPath) + "\n" + FileStamp(trPath);
        lock (CacheLock)
        {
            if (_translationsCache is { } c && c.Stamp == stamp && Covers(c.Keys, keys)) return c.Loc;
            var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_translationsCache is { } old && old.Stamp == stamp) foreach (var k in old.Keys) all.Add(k);
            else foreach (var k in BlueprintTranslationKeys(gdPath)) all.Add(k);
            foreach (var k in keys) all.Add(k);
            if (!Covers(all, keys)) return OreService.LoadTranslations(trPath, keys);
            var loc = OreService.LoadTranslations(trPath, all);
            _translationsCache = (stamp, all, loc);
            return loc;
        }
    }

    // keys のどのキーも、同じ綴り (大文字小文字も同じ) で set に入っているか
    private static bool Covers(HashSet<string> set, HashSet<string> keys)
    {
        foreach (var k in keys)
            if (!set.TryGetValue(k, out var have) || !string.Equals(have, k, StringComparison.Ordinal)) return false;
        return true;
    }

    // BP の表で使いうる訳のキー (blueprints.output_name_key・blueprint_materials.slot_name_key・blueprintPool を含むミッションの title)。読めなければ空
    private static HashSet<string> BlueprintTranslationKeys(string gdPath)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(gdPath)) return keys;
        try
        {
            using var game = new SqliteConnection($"Data Source={gdPath};Mode=ReadOnly;Pooling=False");
            game.Open();
            void Add(string table, string sql)
            {
                if (!TableExists(game, table)) return;
                using var cmd = game.CreateCommand();
                cmd.CommandText = sql;
                using var r = cmd.ExecuteReader();
                while (r.Read()) if (!r.IsDBNull(0) && r.GetString(0) is { Length: > 0 } k) keys.Add(k);
            }
            Add("blueprints", "SELECT output_name_key FROM blueprints");
            Add("blueprint_materials", "SELECT slot_name_key FROM blueprint_materials");
            Add("missions", "SELECT title FROM missions WHERE raw_json LIKE '%blueprintPool%'");
        }
        catch { }
        return keys;
    }

    // === 共通 ===

    private static string Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);

    private static bool TableExists(SqliteConnection db, string table)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @t";
        cmd.Parameters.AddWithValue("@t", table);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0;
    }

    private static bool ColumnExists(SqliteConnection db, string table, string column)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var r = cmd.ExecuteReader();
        while (r.Read()) if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

// ［BP］タブの表の 1 行 (blueprints の 1 件と、その材料・入手・在庫の文字)
public sealed class BlueprintItem
{
    public string Uuid { get; set; } = "";
    public string RecordName { get; set; } = "";       // "CraftingBlueprintRecord." を除いた record 名
    public string Category { get; set; } = "";
    public string OutputRecord { get; set; } = "";     // できる品の record 名 (EntityClassDefinition.…。［詳細］に渡す)
    public string? OutputNameKey { get; set; }
    public string? OutputNameEn { get; set; }
    public long CraftTimeSec { get; set; }
    public string Process { get; set; } = "";
    public int? ChooseCount { get; set; }
    public bool Starter { get; set; }
    public List<BlueprintSlot> Slots { get; } = new();

    public string Name { get; set; } = "";
    public string MaterialsText { get; set; } = "";
    public string? MaterialsTooltip { get; set; }
    public string TimeText { get; set; } = "";
    public string SourceText { get; set; } = "";
    public string? SourceTooltip { get; set; }
    public string StockText { get; set; } = "";
    // 所持 (trade_cache.db の my_blueprints にあるか)。trade_cache.db が無い・読めなければ null
    public bool? Owned { get; set; }
    // 入手のミッション: 表示名 (ツールチップと同じ並び) と、その名前のミッションの record (並んだ順。派生があれば複数)
    public List<(string Name, List<string> Records)> Missions { get; } = new();
    // 材料の欄 (MaterialsText) の中の各枠の文字の位置 (先頭からの文字の番号, 長さ)。Slots と同じ並び
    public List<(int Start, int Length)> MaterialSpans { get; } = new();
}

// 材料の 1 枠 (blueprint_materials の 1 行)。ResourceUuid (資源) か ItemRecord (品目。手掘りの鉱物など) のどちらかが入る
public sealed class BlueprintSlot
{
    public string DebugName { get; set; } = "";
    public string? NameKey { get; set; }
    public string ResourceUuid { get; set; } = "";
    public string ItemRecord { get; set; } = "";
    public double Quantity { get; set; }
    public int MinQuality { get; set; }
    // 品目が手掘りの鉱物なら、照合する resource_types の uuid (ResourceType.<X>)。鉱物でなければ null
    public string? MineralResourceUuid { get; set; }
    // 表示の名前 (LoadData が付ける): 枠名 (訳。無ければ debug 名)・材料の名前 (資源は［鉱石］タブと同じ出し方、品目は 日本語 (英語))
    public string SlotName { get; set; } = "";
    public string MaterialName { get; set; } = "";
    // ［詳細］に出すときの record 名 (資源・手掘りの鉱物を変換した資源の "ResourceType.…"、資源に変換できない品目はその record 名) と名前 (英語名)。出せないものは null
    public string? DetailRecord { get; set; }
    public string? DetailName { get; set; }
}

// 資源の絞り込みの選択肢。Uuid は精錬後の資源 (null = すべて)。原石は「原石名 → 精錬後名」のラベルで精錬後の uuid を指す。
// IsSelected はドロップダウンのチェック (複数選べる)
public sealed class ResourceChoice : INotifyPropertyChanged
{
    public string Label { get; set; } = "";
    public string? Uuid { get; set; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
