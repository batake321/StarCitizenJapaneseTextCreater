using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

// ［BP］タブ。ブループリントの一覧 (gamedata_cache.db の blueprints / blueprint_materials / blueprint_pools。GameDataExtractor.ExtractBlueprintsAsync が作る) を
// FilterSortGrid に出す。列は できる品 (日本語 (英語))・分類・材料・時間・入手 (初期 / ミッション n 件 / 不明)・在庫 (足りる / 一部 / 不足)。
// 在庫は trade_cache.db の ore_stock (［鉱石］タブ) と照合する: 枠ごとに、その資源 (原石は resource_types.refined_uuid で精錬後に寄せる。1 SCU = 1 SCU) の
// 品質 ≥ min_quality の在庫の合計 ≥ 数量なら満たす。手掘りの鉱物 (CraftingCost_Item の Harvestable_Mineral_1H_<X>) は ResourceType.<X> の '個' の在庫で照合する。
// 資源の名前は［鉱石］タブと同じ出し方 (OreService.LoadResources)、枠名・ミッション名は translations.db (OreService.LoadTranslations)。
// DB は作業ディレクトリ (WorkDirProvider。MainWindow が渡す)。初めて表示したときに読み、MainWindow が Reload で読み直す。
// タブを開き直したときは在庫 (ore_stock) だけ読み直す (［鉱石］タブで在庫を書き換えた後に合わせるため)
public partial class BlueprintPanel : UserControl
{
    private const string AllLabel = "(すべて)";
    private const string StockEnough = "足りる";
    private const string StockPartial = "一部";
    private const string StockShort = "不足";

    public Func<string>? WorkDirProvider { get; set; }

    private bool _loadedOnce;
    private int _loadVersion;
    private List<BlueprintItem> _items = new();
    // 在庫の照合に使う (読み直しのために持つ): 原石 uuid → 精錬後 uuid、resource_types の record 名 → uuid
    private Dictionary<string, string> _refinedByUuid = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _resourceUuidByRecord = new(StringComparer.OrdinalIgnoreCase);

    public BlueprintPanel()
    {
        InitializeComponent();
        table.SetColumns(Columns());
        // できる品 (1 列目) のダブルクリック → ［詳細］タブにその品目を出す
        table.NameDoubleClicked += o => { if (o is BlueprintItem r) DetailPanel.Request(r.OutputRecord, r.Name); };
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

    private sealed class LoadResult
    {
        public List<BlueprintItem> Items = new();
        public List<ResourceChoice> Resources = new();
        public List<string> Categories = new();
        public Dictionary<string, string> RefinedByUuid = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> ResourceUuidByRecord = new(StringComparer.OrdinalIgnoreCase);
        public bool StockReadable;
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

    // DB を読んで表の行を作る (裏のスレッド)
    private static LoadResult LoadData(string workDir)
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
        var missionsByPool = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);   // pool_file → ミッションの title (訳のキー)
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
                cmd.CommandText = "SELECT uuid, record_name, category, output_record, output_name_key, output_name_en, craft_time_sec, process, choose_count, starter FROM blueprints";
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
            if (TableExists(game, "blueprint_materials"))
            {
                using var cmd = game.CreateCommand();
                cmd.CommandText = "SELECT bp_uuid, slot_debug_name, slot_name_key, resource_uuid, item_record, quantity, min_quality FROM blueprint_materials ORDER BY rowid";
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
                cmd.CommandText = "SELECT pool_file, bp_uuid FROM blueprint_pools";
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
            if (TableExists(game, "item_index"))
            {
                using var cmd = game.CreateCommand();
                cmd.CommandText = "SELECT record_name, name, name_ja FROM item_index";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var record = Str(r, 0);
                    if (record.Length == 0 || itemNames.ContainsKey(record)) continue;
                    itemNames[record] = (r.IsDBNull(1) ? null : OreService.EnglishText(r.GetString(1)), r.IsDBNull(2) ? null : OreService.JapaneseText(r.GetString(2)));
                }
            }
            // ミッション → 抽選プール (raw_json の BlueprintRewards の blueprintPool のファイル名)
            if (TableExists(game, "missions"))
            {
                using var cmd = game.CreateCommand();
                cmd.CommandText = "SELECT title, raw_json FROM missions WHERE raw_json LIKE '%blueprintPool%'";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var title = Str(r, 0);
                    var raw = Str(r, 1);
                    foreach (Match m in PoolFileRegex.Matches(raw))
                    {
                        var file = m.Groups[1].Value.ToLowerInvariant();
                        if (!missionsByPool.TryGetValue(file, out var list)) missionsByPool[file] = list = new List<string>();
                        list.Add(title);
                    }
                }
            }
        }

        // 資源の名前 (［鉱石］タブと同じ出し方)
        var resources = OreService.LoadResources(gdPath, trPath);
        var resourceByUuid = new Dictionary<string, OreResource>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in resources) resourceByUuid.TryAdd(x.Uuid, x);
        string ResourceName(string uuid) =>
            resourceByUuid.TryGetValue(uuid, out var x) ? x.Display
            : resourceRecords.TryGetValue(uuid, out var rec) && rec.Length > 0 ? OreService.ShortRecord(rec)
            : (uuid.Length > 8 ? uuid[..8] : uuid);

        // 訳: 枠名・ミッション名・できる品の名前のキー (日本語と英語を今の訳から引く)
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in bps)
        {
            foreach (var s in it.Slots) if (s.NameKey is { Length: > 0 } k) keys.Add(k);
            if (it.OutputNameKey is { Length: > 0 } ok) keys.Add(ok);
        }
        foreach (var list in missionsByPool.Values) foreach (var t in list) if (t.Length > 0) keys.Add(t);
        var loc = OreService.LoadTranslations(trPath, keys);

        // ミッションの title → 表示名 (日本語 → 英語 → キー)。ミッションタブの一覧と同じく、強調タグを消し ~mission(…) を〔〕にする (MissionDisplayText.ToPlain)
        string MissionName(string title)
        {
            if (loc.TryGetValue(title, out var t))
                return MissionDisplayText.ToPlain(OreService.JapaneseText(t.Ja) ?? OreService.EnglishText(t.En) ?? title);
            return title;
        }

        // 行の文字を作る
        var usedResources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in bps)
        {
            // できる品の名前: 日本語 (英語)。日本語は訳 (name_key) → item_index.name_ja、英語は訳の english → blueprints.output_name_en → item_index.name。
            // どちらも無ければ record 名 ("EntityClassDefinition." を除く)
            string? ja = null, en = null;
            if (it.OutputNameKey is { Length: > 0 } nk && loc.TryGetValue(nk, out var tr))
            {
                ja = OreService.JapaneseText(tr.Ja);
                en = OreService.EnglishText(tr.En);
            }
            itemNames.TryGetValue(it.OutputRecord, out var inm);
            ja ??= inm.Ja;
            en ??= it.OutputNameEn ?? inm.En;
            it.Name = OreService.Label(ja, en) ?? (it.OutputRecord.Length > 0 ? OreService.ShortRecord(it.OutputRecord) : it.RecordName);

            // 材料: 枠ごとに「資源名 数量 単位」(品質の下限があれば「品質 n 以上」)。ツールチップには枠名を添える
            var parts = new List<string>();
            var tips = new List<string>();
            foreach (var s in it.Slots)
            {
                string text;
                if (s.ResourceUuid.Length > 0)
                {
                    usedResources.Add(s.ResourceUuid);
                    text = $"{ResourceName(s.ResourceUuid)} {FormatQuantity(s.Quantity)} {OreService.UnitScu}";
                }
                else
                {
                    s.MineralResourceUuid = MineralResourceUuid(s.ItemRecord, res.ResourceUuidByRecord);
                    var name = s.MineralResourceUuid != null ? ResourceName(s.MineralResourceUuid)
                        : itemNames.TryGetValue(s.ItemRecord, out var im) ? (OreService.Label(im.Ja, im.En) ?? OreService.ShortRecord(s.ItemRecord))
                        : OreService.ShortRecord(s.ItemRecord);
                    text = $"{name} {FormatQuantity(s.Quantity)} {OreService.UnitPiece}";
                }
                if (s.MinQuality > 0) text += $" 品質 {s.MinQuality} 以上";
                parts.Add(text);
                var slotName = s.NameKey is { Length: > 0 } sk && loc.TryGetValue(sk, out var st) ? (OreService.JapaneseText(st.Ja) ?? OreService.EnglishText(st.En) ?? s.DebugName) : s.DebugName;
                tips.Add(slotName.Length > 0 ? $"{slotName}: {text}" : text);
            }
            var prefix = it.ChooseCount is { } cc ? $"{cc} 枠選択: " : "";
            it.MaterialsText = prefix + string.Join(" / ", parts);
            it.MaterialsTooltip = parts.Count == 0 ? null : (it.ChooseCount is { } cc2 ? $"{tips.Count} 枠のうち {cc2} 枠を選ぶ\n" : "") + string.Join("\n", tips);

            it.TimeText = FormatTime(it.CraftTimeSec);
        }

        // 入手: 初期所持 / ミッション (抽選プール経由) の件数とミッション名
        var missionsByBp = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, list) in poolBps)
        {
            if (!missionsByPool.TryGetValue(file, out var titles)) continue;
            foreach (var it in list)
            {
                if (!missionsByBp.TryGetValue(it.Uuid, out var acc)) missionsByBp[it.Uuid] = acc = new List<string>();
                acc.AddRange(titles);
            }
        }
        foreach (var it in bps)
        {
            var sources = new List<string>();
            if (it.Starter) sources.Add("初期");
            if (missionsByBp.TryGetValue(it.Uuid, out var titles) && titles.Count > 0)
            {
                var names = titles.Select(MissionName).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                // 件数はツールチップに並べる名前の数 (同じ名前の派生ミッションは 1 件)
                sources.Add($"ミッション {names.Count} 件");
                it.SourceTooltip = string.Join("\n", names);
            }
            it.SourceText = sources.Count > 0 ? string.Join(" / ", sources) : "不明";
        }

        // 在庫 (ore_stock) との照合
        var stock = OreService.LoadStockReadOnly(tcPath);
        res.StockReadable = stock != null;
        var stockIndex = BuildStockIndex(stock, res.RefinedByUuid);
        foreach (var it in bps) it.StockText = StockText(it, stockIndex, res.ResourceUuidByRecord, stock != null);

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
    private sealed class StockIndex
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
            List<(int? Quality, double Quantity)>? rows = null;
            if (s.ResourceUuid.Length > 0) index.Scu.TryGetValue(s.ResourceUuid, out rows);
            else
            {
                s.MineralResourceUuid ??= MineralResourceUuid(s.ItemRecord, resourceUuidByRecord);
                if (s.MineralResourceUuid != null) index.Piece.TryGetValue(s.MineralResourceUuid, out rows);
            }
            if (rows == null) continue;
            double have = 0;
            foreach (var (quality, quantity) in rows)
                if ((quality ?? 0) >= s.MinQuality) have += quantity;
            if (have + 1e-9 >= s.Quantity) satisfied++;
        }
        var required = it.ChooseCount is { } cc && cc > 0 && cc < it.Slots.Count ? cc : it.Slots.Count;
        return satisfied >= required ? StockEnough : satisfied > 0 ? StockPartial : StockShort;
    }

    // === 絞り込み ===

    private void RefreshResourceCombo(List<ResourceChoice> choices)
    {
        var prev = (cmbResource.SelectedItem as ResourceChoice)?.Uuid;
        var prevLabel = (cmbResource.SelectedItem as ResourceChoice)?.Label;
        var list = new List<ResourceChoice> { new() { Label = AllLabel, Uuid = null } };
        list.AddRange(choices);
        cmbResource.ItemsSource = list;
        var keep = prev != null ? list.FirstOrDefault(c => string.Equals(c.Uuid, prev, StringComparison.OrdinalIgnoreCase) && c.Label == prevLabel)
            ?? list.FirstOrDefault(c => string.Equals(c.Uuid, prev, StringComparison.OrdinalIgnoreCase)) : null;
        cmbResource.SelectedItem = keep ?? list[0];
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
        if (cmbResource?.SelectedItem is ResourceChoice { Uuid: { Length: > 0 } uuid })
            items = items.Where(it => it.Slots.Any(s => string.Equals(s.ResourceUuid, uuid, StringComparison.OrdinalIgnoreCase)));
        if (cmbCategory?.SelectedItem is string cat && cat != AllLabel)
            items = items.Where(it => string.Equals(it.Category, cat, StringComparison.Ordinal));
        if (chkCraftable?.IsChecked == true)
            items = items.Where(it => it.StockText == StockEnough);
        table.SetItems(items.ToList());
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
    ];

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
}

// 資源の絞り込みの選択肢。Uuid は精錬後の資源 (null = すべて)。原石は「原石名 → 精錬後名」のラベルで精錬後の uuid を指す
public sealed class ResourceChoice
{
    public string Label { get; set; } = "";
    public string? Uuid { get; set; }
}
