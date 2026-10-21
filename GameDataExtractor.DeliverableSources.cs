using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

// Wikelo の契約の納品物の「ゲームデータ上の入手場所」(契約タブの納品物の入手場所の欄に出す)。インデックス構築の中で作る (ExtractDeliverableSourcesAsync)。
// 調べ方は調査 (Python のスクリプト s1〜s7・t1〜t12。まとめは t11_all.py) をそのまま移したもの:
//   DCB を JSON に書き出し (starbreaker dcb extract --format json。空きを抑えるためファイルのパスで小分けにし、読んでは消す。DeliverableDcbChunks)、Data.p4k の全 socpak (無圧縮で入っている) を読む。
//   socpak の中のバイト列から DCB のレコードの uuid の文字列と、子の socpak のパスの文字列 (ObjectContainers/…socpak) を探し、
//   「レコードが出てくる socpak」と「socpak の親子」を作る。親を辿って惑星・月 (pu/system/<星系>/<名前>.socpak。それより深い pu/system の下の socpak は、
//   そこに uuid の出る StarMapObject の parent を辿って当たる、種類が Planet / Moon の StarMapObject) と、
//   いちばん近い名前付きの場所 (uuid が出てくる StarMapObject の name) まで上がる。
//   A 参照: 品 (資源は MineableElement) を参照するレコードを 6 段まで上へ辿り、socpak に出てくるもの (採掘の岩・収穫・スロットプリセット等)
//   B 生き物: ロードアウトの XML の itemName → loadoutPath でそれを使う actor → 惑星の .ale の tokenPairs (場所は .ale の入っている socpak) / actor の配置 /
//     SpawnDescription_Entity (タグで選ぶ。推定) のあるレコードの配置
//   C ルート: 全レコードの LootConfig を、調査で決めたタグの読み方 (H1〜H5。推定) で評価し、品が選ばれうる持ち主の配置
//   D 配置: 品そのものの uuid が出てくる socpak
// 場所の名前は英語ローカライズ (訳キーの english) で引いた英語名。対象は契約の行 (missions) から決めた Wikelo の契約の納品物だけ
public partial class GameDataExtractor
{
    // 納品物の入手場所 (ゲームデータから)。1 行 = 1 つの品 (target) の 1 つの節 (section) の 1 つの場所:
    //   target_kind: 'entity' (品。target はエンティティのファイル名の小文字) / 'resource' (資源。target は ResourceType の record 名の小文字)
    //   section: 画面の節 (DeliverableSourceSections)。kind: 調査の種類の名前 (配置 / 参照:<型> / ルート / 生態 / 出現:<型> など。t11_all.py と同じ文字)
    //   place_type: 'planet' (惑星・月など。英語名。星図の名前か、socpak の名前を翻訳のキーとして引いたもの) / 'planet_raw' (惑星・月などで名前の引けない socpak の名前) / 'near' (近い名前付きの場所。英語名) /
    //     'unlocated' (場所まで辿れないもの。name は調査の via の文字。socpak まで分かったものは「 [socpak]」付き)
    // インデックス構築で作り直す (取り込めなかったときは前の行を残す)
    internal const string DeliverableSourcesDdl = """

        CREATE TABLE IF NOT EXISTS deliverable_sources (
            target_kind TEXT NOT NULL,
            target TEXT NOT NULL,
            section TEXT NOT NULL,
            kind TEXT NOT NULL,
            place_type TEXT NOT NULL,
            name TEXT NOT NULL,
            PRIMARY KEY (target_kind, target, section, kind, place_type, name)
        );
        """;

    // 画面の節 (MissionService.FormatDeliverableSources がこの順に出す)
    internal static class DeliverableSourceSections
    {
        public const string Mining = "mining";          // 採掘・収穫 (参照の鎖に採掘・収穫のレコードがある。資源)
        public const string Creature = "creature";      // 生き物 (惑星の生態 .ale)
        public const string Placed = "placed";          // 配置 (品そのもの・品を参照するエンティティの配置)
        public const string Holder = "holder";          // この品を持つ生き物・NPC の配置 (ロードアウト)
        public const string Spawn = "spawn";            // 出現 (タグで選ぶ出現の設定。推定)
        public const string LootListed = "loot_listed"; // ルート (表に品の名前が載っている。参照の鎖に表がある)
        public const string Loot = "loot";              // ルート (タグの評価。推定)
    }

    // 作業用のフォルダ (DCB の JSON の小分けと、読んだ JSON を縮めて置く records.bin と Loadouts を置く。終わったら消す)
    private string DeliverableTmpDir => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(DbPath)) ?? ".", "deliverable_tmp" + _tmpSuffix);

    // 小分け 1 回分の書き出し (と Loadouts) に要る空き。足りなければ取り込みを飛ばす (前のデータを残す)。
    // 下の分け方で、調査の全件 JSON (約 3.2GB) のいちばん大きい小分けは約 0.5GB (entities/scitem/characters)。その倍に縮めた JSON の置き場の分を見込む
    private const long DeliverableChunkBytes = 1L * 1024 * 1024 * 1024;

    // DCB の書き出しの小分け (starbreaker dcb extract --filter の glob)。--filter はレコードのファイルのパス
    // (libs/foundry/records/…/x.xml。starbreaker v0.2.2 の cli/src/dcb.rs の extract は file_name に glob-match 0.2.1 の glob_match を当てる) で絞る。
    // glob-match は大文字小文字を区別し、* は / を越えず、末尾の /** は下の全部に当たり、[!…] は 1 字の否定。
    // 各段で「その段のファイル (X*)」と「1 字目の組で分けたフォルダ (X[…]*/**)」に分け、組は否定の組 ([!…]) で残りを全部受けるので、
    // libs/foundry/records の下のファイルは漏れなく、重ならずにどれか 1 つに入る (前の作り方も libs/foundry/records の下しか読んでいない)。
    // 大きいフォルダ (entities/scitem・ui/…) だけ 1 段下で分ける。() 内は調査の全件 JSON での大きさ (MB)
    private static readonly string[] DeliverableDcbChunks =
    [
        "libs/foundry/records/*",                        // records 直下のファイル
        "libs/foundry/records/[a-d]*/**",                // actor など (426)
        "libs/foundry/records/[f-tv-z]*/**",             // missionbroker など (207)
        "libs/foundry/records/[!a-z]*/**",               // 英小文字で始まらないフォルダ (0)
        "libs/foundry/records/e*/*",                     // entities などの直下のファイル (3)
        "libs/foundry/records/e*/[!s]*/**",              // (25)
        "libs/foundry/records/e*/s*/*",                  // spaceships などの直下のファイル (349)
        "libs/foundry/records/e*/s*/[!cdsw]*/**",        // scitem/usables など (193)
        "libs/foundry/records/e*/s*/[dw]*/**",           // scitem/doors・weapons (315)
        "libs/foundry/records/e*/s*/s*/**",              // scitem/ships など (358)
        "libs/foundry/records/e*/s*/c*/*",
        "libs/foundry/records/e*/s*/c*/h*/**",           // scitem/characters/human など (487)
        "libs/foundry/records/e*/s*/c*/[!h]*/**",        // scitem/carryables など (114)
        "libs/foundry/records/u*/*",
        "libs/foundry/records/u*/[!b]*/**",              // (3)
        "libs/foundry/records/u*/b*/*",
        "libs/foundry/records/u*/b*/[a-f]*/**",          // ui/buildingblocks の下 (235)
        "libs/foundry/records/u*/b*/[g-l]*/**",          // (236)
        "libs/foundry/records/u*/b*/[!a-l]*/**",         // (258)
    ];

    // 空きを確かめる。足りなければ知らせて false (取り込みを飛ばす)
    private bool HasDeliverableSpace(string dir, string what)
    {
        var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace;
        if (free >= DeliverableChunkBytes) return true;
        StatusChanged?.Invoke($"[納品物の入手場所] 作業の空きが足りないため飛ばしました ({what}。要 {DeliverableChunkBytes / 1024 / 1024}MB・空き {free / 1024 / 1024}MB。前のデータを残します)");
        return false;
    }

    // Wikelo の納品物の入手場所を作り、deliverable_sources を入れ替える。入れた行数を返す。失敗したら例外 (呼び出し側で知らせて構築は続ける)。
    // 同じ DCB と socpak の読み込みを使って、資源の採掘できる場所 (ore_locations。FindOreLocations) も作って入れ替える。
    // 採掘できる場所の作成・書き込みの失敗は知らせるだけで例外にしない (ore_locations は前のまま。納品物の入手場所の扱いは変えない)。
    // Wikelo の納品物が無いときは、deliverable_sources は前のまま (ロードアウトの読み込みと照合も飛ばす) で、採掘できる場所だけ作る。
    // skipWriteIfIndexBuildStarts: 起動後の裏の作り直し (RebuildOreLocationsAsync) で、途中で構築・再抽出が始まったら書き込まずに 0 を返す。
    // 各段の時間を StatusChanged に出す (構築の時間がどれだけ増えたかを実測できるように)
    private async Task<int> ExtractDeliverableSourcesAsync(string p4kPath, CancellationToken ct, bool skipWriteIfIndexBuildStarts = false)
    {
        const string P = "[納品物の入手場所]";
        var total = Stopwatch.StartNew();
        var (entities, resources) = ReadDeliverableTargets();
        var hasTargets = entities.Count + resources.Count > 0;
        if (!hasTargets)
            StatusChanged?.Invoke($"{P} Wikelo の契約の納品物がありません (前のデータを残します。採掘できる場所 (ore_locations) だけ作ります)");
        else
            StatusChanged?.Invoke($"{P} 対象: 品 {entities.Count} 件・資源 {resources.Count} 件");
        var dir = DeliverableTmpDir;
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);

            // 1・2. DCB を小分けに JSON に書き出し、読んで (索引を作り、JSON は縮めて records.bin に置く)、小分けのフォルダを消す、を繰り返す
            var sw = Stopwatch.StartNew();
            double tDump = 0, tLoad = 0;
            var jsonDir = Path.Combine(dir, "dcb");
            using var dcb = new DeliverableDcb(Path.Combine(dir, "records.bin"));
            for (int i = 0; i < DeliverableDcbChunks.Length; i++)
            {
                var filter = DeliverableDcbChunks[i];
                if (!HasDeliverableSpace(dir, $"DCB の小分け {i + 1}/{DeliverableDcbChunks.Length}")) return 0;
                sw.Restart();
                StatusChanged?.Invoke($"{P} DCB を JSON に書き出し中 ({i + 1}/{DeliverableDcbChunks.Length})...");
                if (Directory.Exists(jsonDir)) Directory.Delete(jsonDir, true);
                await RunStarBreakerAsync(_dcbPath is { } dcbFile
                    ? $"dcb extract --dcb \"{dcbFile}\" -o \"{jsonDir}\" --format json --filter \"{filter}\""
                    : $"dcb extract --p4k \"{p4kPath}\" -o \"{jsonDir}\" --format json --filter \"{filter}\"", ct);
                tDump += sw.Elapsed.TotalSeconds;
                sw.Restart();
                // 当たるレコードの無い小分けではフォルダができない (0 件の書き出しは成功で終わる) ので、そのときは読まない
                var recordsRoot = Path.Combine(jsonDir, "libs", "foundry", "records");
                if (Directory.Exists(recordsRoot)) await Task.Run(() => dcb.LoadChunk(recordsRoot, ct), ct);
                Directory.Delete(jsonDir, true);
                tLoad += sw.Elapsed.TotalSeconds;
            }
            sw.Restart();
            await Task.Run(() => dcb.Finish(), ct);
            tLoad += sw.Elapsed.TotalSeconds;
            if (dcb.RecordCount == 0) throw new InvalidOperationException("DCB の JSON を書き出せませんでした (libs/foundry/records のレコードがありません)");

            // 3. ロードアウトの XML (Data/Scripts/Loadouts/**) の itemName (納品物の照合にだけ使う。納品物が無ければ読まない)
            var loadoutItems = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            int loadoutFiles = 0, loadoutBad = 0;
            double tLoadout = 0;
            if (hasTargets)
            {
                if (!HasDeliverableSpace(dir, "ロードアウトの XML")) return 0;
                sw.Restart();
                StatusChanged?.Invoke($"{P} ロードアウトの XML を読み込み中...");
                var loDir = Path.Combine(dir, "loadouts");
                Directory.CreateDirectory(loDir);
                await RunStarBreakerAsync($"p4k extract --p4k \"{p4kPath}\" -o \"{loDir}\" --filter \"Data/Scripts/Loadouts/**\"", ct);
                (loadoutItems, loadoutFiles, loadoutBad) = await Task.Run(() => DeliverableDcb.ReadLoadoutItemNames(Path.Combine(loDir, "Data"), ct), ct);
                tLoadout = sw.Elapsed.TotalSeconds;
            }

            // 4. socpak
            sw.Restart();
            StatusChanged?.Invoke($"{P} Data.p4k の socpak を読み込み中...");
            var soc = await Task.Run(() => DeliverableSocpaks.Scan(p4kPath, dcb, ct), ct);
            var tSoc = sw.Elapsed.TotalSeconds;

            // 5. 照合
            sw.Restart();
            var loc = LoadShipNameLocalization();
            var finder = new DeliverableSourceFinder(dcb, soc, loadoutItems, loc);
            var rows = hasTargets ? await Task.Run(() => finder.Find(entities, resources, ct), ct) : new List<DeliverableSourceRow>();
            var tFind = sw.Elapsed.TotalSeconds;

            // 5b. 資源の採掘できる場所 (失敗しても納品物の入手場所は続ける)
            sw.Restart();
            List<OreLocationRow>? oreRows = null;
            try { oreRows = await Task.Run(() => finder.FindOreLocations(ct), ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { StatusChanged?.Invoke($"[採掘できる場所] 作れませんでした (前のデータを残します): {ex.Message}"); }
            var tOre = sw.Elapsed.TotalSeconds;

            // 6. 書き込み (入れ替え)
            ct.ThrowIfCancellationRequested();
            if (skipWriteIfIndexBuildStarts && IsIndexBuildRunning)
            {
                StatusChanged?.Invoke($"{P} インデックス構築・装備データの再抽出が始まったので、作った値は書き込みませんでした");
                return 0;
            }
            if (oreRows is { Count: 0 })
            {
                // 作れた行が 0 行: 前の行を消さずに残す (失敗として知らせる)。表がまだ無ければ空の表だけ作る (起動後の補完が毎回走らないように)
                try
                {
                    using var ddl = _db!.CreateCommand();
                    ddl.CommandText = OreLocationsDdl;
                    ddl.ExecuteNonQuery();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    StatusChanged?.Invoke($"[採掘できる場所] 表を作れませんでした: {ex.Message}");
                }
                StatusChanged?.Invoke("[採掘できる場所] 1 行も作れませんでした (前のデータを残します)");
            }
            else if (oreRows != null)
            {
                try
                {
                    WriteOreLocations(oreRows);
                    var places = oreRows.Select(r => (r.Preset, r.Place)).Distinct().Count();
                    StatusChanged?.Invoke($"[採掘できる場所] {oreRows.Count} 行 (資源 {oreRows.Select(r => r.ResourceUuid).Distinct().Count()} 種・プリセットと場所の組 {places} 件)。時間 {tOre:F1}秒"
                        + (loc == null ? " (英語ローカライズが見つからないので岩の種類はレコード名のまま)" : ""));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    StatusChanged?.Invoke($"[採掘できる場所] 書き込めませんでした (前のデータを残します): {ex.Message}");
                }
            }
            if (!hasTargets) return 0;
            using (var tx = _db!.BeginTransaction())
            {
                using (var ddl = _db.CreateCommand())
                {
                    ddl.Transaction = tx;
                    ddl.CommandText = DeliverableSourcesDdl + "DELETE FROM deliverable_sources;";
                    ddl.ExecuteNonQuery();
                }
                using var ins = _db.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = "INSERT OR IGNORE INTO deliverable_sources(target_kind,target,section,kind,place_type,name) VALUES(@tk,@t,@s,@k,@p,@n)";
                var pTk = ins.Parameters.Add("@tk", SqliteType.Text);
                var pT = ins.Parameters.Add("@t", SqliteType.Text);
                var pS = ins.Parameters.Add("@s", SqliteType.Text);
                var pK = ins.Parameters.Add("@k", SqliteType.Text);
                var pP = ins.Parameters.Add("@p", SqliteType.Text);
                var pN = ins.Parameters.Add("@n", SqliteType.Text);
                foreach (var r in rows)
                {
                    pTk.Value = r.TargetKind; pT.Value = r.Target; pS.Value = r.Section; pK.Value = r.Kind; pP.Value = r.PlaceType; pN.Value = r.Name;
                    ins.ExecuteNonQuery();
                }
                tx.Commit();
            }
            var withRows = rows.Select(r => (r.TargetKind, r.Target)).Distinct().Count();
            StatusChanged?.Invoke($"{P} {rows.Count} 行 (場所の分かった品・資源 {withRows} / {entities.Count + resources.Count} 件)。"
                + $"時間: DCB の書き出し {tDump:F1}秒・JSON の読み込み {tLoad:F1}秒 ({dcb.RecordCount} 件)・ロードアウト {tLoadout:F1}秒 ({loadoutFiles} 件"
                + (loadoutBad > 0 ? $"、読めないもの {loadoutBad} 件" : "") + $")・socpak {tSoc:F1}秒 ({soc.SocpakCount} 件"
                + (soc.BadCount > 0 ? $"、読めないもの {soc.BadCount} 件" : "") + $")・照合 {tFind:F1}秒・合計 {total.Elapsed.TotalSeconds:F1}秒"
                + (loc == null ? " (英語ローカライズが見つからないので場所の名前は訳キーのまま)" : ""));
            return rows.Count;
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    // 対象: missions の Wikelo の契約の納品物 (契約側の HaulingOverride とテンプレート側の _TemplateHauling_。MissionService.DeliverableRefsForBuild)。
    // 品はエンティティのファイル名、資源は ResourceType の record 名 (どちらも初めに出た綴り。重なりは大文字小文字を無視して 1 つ)
    private (List<string> Entities, List<string> Resources) ReadDeliverableTargets()
    {
        var entities = new List<string>();
        var resources = new List<string>();
        var seenE = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenR = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "SELECT record_name, COALESCE(mission_type, ''), COALESCE(raw_json, '') FROM missions";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            foreach (var (isResource, reference) in MissionService.DeliverableRefsForBuild(r.GetString(0), r.GetString(1), r.GetString(2)))
            {
                if (isResource) { if (seenR.Add(reference)) resources.Add(reference); }
                else if (seenE.Add(reference)) entities.Add(reference);
            }
        }
        return (entities, resources);
    }

    // 入手場所の 1 行 (deliverable_sources)
    internal readonly record struct DeliverableSourceRow(string TargetKind, string Target, string Section, string Kind, string PlaceType, string Name);

    // ── 資源の採掘できる場所 (ore_locations。［詳細］タブの資源の【採掘できる場所】。DeliverableSourceFinder.FindOreLocations が作る) ──
    // 1 行 = (資源, 場所, 掘り方, 岩の種類) を 1 つの HarvestableProviderPreset (preset) について:
    //   resource_uuid / resource_record: MineableElement の resourceType の uuid と record 名 ("ResourceType.…")
    //   preset: HarvestableProviderPreset の record 名。place: 場所の英語名 (Where の決め方。見つからなければ preset と同じ record 名)
    //   system: 星系名 (Stanton / Pyro / Nyx など。分からなければ '')
    //   method: '船で採掘' (SpaceShip_Mineables) / 'ROC で採掘' (GroundVehicle_Mineables) / '手で採掘' (FPS_Mineables)
    //   rock: 岩の種類の英語名 (MineableComposition の depositName を英語ローカライズで引いたもの。引けなければ MineableComposition の record 名)
    //   chance: その場所でその掘り方の岩のうち、この岩の種類が出る割合 % (relativeProbability ÷ 同じグループの relativeProbability の合計 × 100。
    //     同じ岩の種類が同じグループに 2 つ以上あれば足す)
    //   min_pct / max_pct: 岩の中の資源の割合の範囲 % (compositionArray の minPercentage / maxPercentage。同じ資源の行が 2 つ以上あれば最小 / 最大)
    //   part_probability: その compositionArray の行の probability (2 つ以上あれば最大)
    //   group_probability: グループの groupProbability (式が分からないので計算には使わない。保存だけ)
    // インデックス構築 (Step 6c) で作り直す (作れなかったときは前の行を残す)。前の版の DB には表が無いので、起動後に一度だけ作る (RebuildOreLocationsAsync)
    internal const string OreLocationsTable = "ore_locations";
    internal const string OreLocationsDdl = """

        CREATE TABLE IF NOT EXISTS ore_locations (
            resource_uuid TEXT NOT NULL,
            resource_record TEXT NOT NULL,
            preset TEXT NOT NULL,
            place TEXT NOT NULL,
            system TEXT NOT NULL,
            method TEXT NOT NULL,
            rock TEXT NOT NULL,
            chance REAL NOT NULL,
            min_pct REAL NOT NULL,
            max_pct REAL NOT NULL,
            part_probability REAL NOT NULL,
            group_probability REAL NOT NULL,
            PRIMARY KEY (resource_uuid, preset, place, method, rock)
        );
        CREATE INDEX IF NOT EXISTS idx_ore_locations_resource ON ore_locations(resource_uuid);
        """;

    // 掘り方 (対象のグループ名 → 画面の文字)。この 3 つのグループだけを見る
    internal const string OreMethodShip = "船で採掘", OreMethodRoc = "ROC で採掘", OreMethodHand = "手で採掘";
    internal static readonly (string Group, string Method)[] OreGroups =
    [
        ("SpaceShip_Mineables", OreMethodShip),
        ("GroundVehicle_Mineables", OreMethodRoc),
        ("FPS_Mineables", OreMethodHand),
    ];

    // 番号の付いた小さな場所の種類 (名前の頭 → 表で件数にまとめるときの名前)。［詳細］タブの資源の表 (GameDataQueryService.ResourceDetailText) で使う
    internal static readonly (string Prefix, string Label)[] OreSmallPlacePrefixes =
    [
        ("Mining Claim #", "Mining Claim"),
        ("Mining Base #", "Mining Base"),
        ("RMB-", "RMB"),
        ("RAB-", "RAB"),
        ("Cluster ", "Cluster"),
    ];

    internal readonly record struct OreLocationRow(string ResourceUuid, string ResourceRecord, string Preset, string Place, string System, string Method,
        string Rock, double Chance, double MinPct, double MaxPct, double PartProbability, double GroupProbability);

    // ore_locations を入れ替える (表が無ければ作る)
    private void WriteOreLocations(List<OreLocationRow> rows)
    {
        using var tx = _db!.BeginTransaction();
        using (var ddl = _db.CreateCommand())
        {
            ddl.Transaction = tx;
            ddl.CommandText = OreLocationsDdl + "DELETE FROM ore_locations;";
            ddl.ExecuteNonQuery();
        }
        using (var ins = _db.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR IGNORE INTO ore_locations(resource_uuid,resource_record,preset,place,system,method,rock,chance,min_pct,max_pct,part_probability,group_probability) "
                + "VALUES(@u,@r,@p,@pl,@s,@m,@k,@c,@mn,@mx,@pp,@gp)";
            var pU = ins.Parameters.Add("@u", SqliteType.Text);
            var pR = ins.Parameters.Add("@r", SqliteType.Text);
            var pP = ins.Parameters.Add("@p", SqliteType.Text);
            var pPl = ins.Parameters.Add("@pl", SqliteType.Text);
            var pS = ins.Parameters.Add("@s", SqliteType.Text);
            var pM = ins.Parameters.Add("@m", SqliteType.Text);
            var pK = ins.Parameters.Add("@k", SqliteType.Text);
            var pC = ins.Parameters.Add("@c", SqliteType.Real);
            var pMn = ins.Parameters.Add("@mn", SqliteType.Real);
            var pMx = ins.Parameters.Add("@mx", SqliteType.Real);
            var pPp = ins.Parameters.Add("@pp", SqliteType.Real);
            var pGp = ins.Parameters.Add("@gp", SqliteType.Real);
            foreach (var r in rows)
            {
                pU.Value = r.ResourceUuid; pR.Value = r.ResourceRecord; pP.Value = r.Preset; pPl.Value = r.Place; pS.Value = r.System; pM.Value = r.Method; pK.Value = r.Rock;
                pC.Value = r.Chance; pMn.Value = r.MinPct; pMx.Value = r.MaxPct; pPp.Value = r.PartProbability; pGp.Value = r.GroupProbability;
                ins.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    // インデックスを構築した DB で、ore_locations の表がまだ無いか (前の版で構築した DB・前の版のバックアップを取り込んだ DB)。
    // 起動後の RebuildOreLocationsAsync を走らせるか決めるため。表は EnsureDb では作らない (無いことを印にする)
    public bool NeedsOreLocations()
    {
        EnsureDb();
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM item_index) AND NOT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @t)";
        cmd.Parameters.AddWithValue("@t", OreLocationsTable);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) != 0;
    }

    // 採掘できる場所 (ore_locations) を作る。DCB と socpak の読み込みが納品物の入手場所 (Step 6c) と同じなので、Step 6c をそのまま走らせて
    // deliverable_sources と ore_locations を一緒に作り直す。起動後に裏で一度だけ呼ばれる (MainWindow: NeedsOreLocations のとき)。
    // 構築・再抽出と同時に走っても作業フォルダがぶつからないよう、取り出し先は別の名前 (dcb_tmp_orelocations / deliverable_tmp_orelocations) にする。
    // 途中で構築・再抽出が始まったら (IsIndexBuildRunning)、書き込まずに 0 を返す。作れなかったとき (取り消し以外) も、次の起動で走り直さないよう
    // 表だけは作る (空のまま。次のインデックス構築で作られる)。入った ore_locations の行数を返す
    public async Task<int> RebuildOreLocationsAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var p4kPath = FindDataP4k()
            ?? throw new FileNotFoundException("Data.p4k が見つかりません。設定のゲームパスを確認してください。");

        await EnsureStarBreakerAsync();
        EnsureDb();

        Action<string>? relay = progress != null ? msg => progress.Report(msg) : null;
        if (relay != null) StatusChanged += relay;
        var prevSuffix = _tmpSuffix;
        if (_dcbScopeDepth == 0) _tmpSuffix = "_orelocations";
        try
        {
            try
            {
                using var dcbScope = await UseExtractedDcbAsync(p4kPath, ct);
                await ExtractDeliverableSourcesAsync(p4kPath, ct, skipWriteIfIndexBuildStarts: true);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { StatusChanged?.Invoke($"[採掘できる場所・納品物の入手場所] 作れませんでした: {ex.Message}"); }
            if (IsIndexBuildRunning) return 0;
            using (var ddl = _db!.CreateCommand())
            {
                ddl.CommandText = OreLocationsDdl;
                ddl.ExecuteNonQuery();
            }
            using var cnt = _db.CreateCommand();
            cnt.CommandText = "SELECT COUNT(*) FROM ore_locations";
            return Convert.ToInt32(cnt.ExecuteScalar() ?? 0L);
        }
        finally
        {
            _tmpSuffix = prevSuffix;
            if (relay != null) StatusChanged -= relay;
        }
    }

    // ── DCB の JSON (小分けを全部読んだもの) から作る索引 (調査の rev.json・t1_enttags.json・tags.json などに当たる) ──
    // 小分けごとに LoadChunk で読み、全部読んだら Finish で索引を作る。JSON の中身は縮めて records.bin に置き、後で ReadText で読む
    internal sealed class DeliverableDcb : IDisposable
    {
        // ファイル (records の下の相対パス。/ 区切り。調査の rel) の並びは、調査 (Python の os.walk) と同じく、フォルダごとにファイル → 子フォルダの順
        // (小分けで読むので、Finish で全件の木を歩いたときの並び (WalkOrder) に並べ直す)
        public List<string> Files { get; } = new();
        public int RecordCount => Files.Count;
        // 縮めた JSON の置き場 (rel → records.bin の中の位置と長さ)。前の作り方はファイルを Windows のパスで開いていた (大文字小文字を区別しない) ので、引き方もそれに合わせる
        private readonly FileStream _store;
        private readonly Dictionary<string, (long Offset, int Length)> _storeIndex = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(string Rel, FileInfoResult Res)> _pending = new();
        private readonly object _storeLock = new();

        public DeliverableDcb(string storePath)
        {
            _store = new FileStream(storePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        }

        public void Dispose() => _store.Dispose();

        // rel の JSON の文字 (無ければ null)
        public string? ReadText(string rel)
        {
            if (!_storeIndex.TryGetValue(rel, out var at)) return null;
            var buf = new byte[at.Length];
            lock (_storeLock)
            {
                _store.Position = at.Offset;
                _store.ReadExactly(buf);
            }
            using var z = new DeflateStream(new MemoryStream(buf, writable: false), CompressionMode.Decompress);
            using var sr = new StreamReader(z, Encoding.UTF8);
            return sr.ReadToEnd();
        }
        // rel → _RecordName_ (調査の d['rec']。最初に出る "_RecordName_" の値。無ければ null)
        public Dictionary<string, string?> Rec { get; } = new(StringComparer.Ordinal);
        // 参照先のファイル名 (小文字・.json を除く) → 参照元の rel (ordinal で並べたもの。調査の d['rev'])
        public Dictionary<string, List<string>> Rev { get; } = new(StringComparer.Ordinal);
        // レコードの uuid (先頭 600 文字の "_RecordId_") → record 名 (調査の t10 の ids)
        public Dictionary<string, string> IdToRecord { get; } = new(StringComparer.Ordinal);
        // actor/・entities/・harvestable/ のレコードの uuid (先頭 400 文字) → record 名 (調査の s5 の id2rec)
        public Dictionary<string, string> AleIdToRecord { get; } = new(StringComparer.Ordinal);
        // ファイル名 (小文字) → record 名 / record 名 → rel (どちらも後に出たもの。調査の base2rec / rec2file)
        public Dictionary<string, string> BaseToRecord { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> RecordToFile { get; } = new(StringComparer.Ordinal);
        // ファイル名 (小文字) → rel (後に出たもの。調査の t2b の fileidx)
        public Dictionary<string, string> BaseToRel { get; } = new(StringComparer.Ordinal);
        // ファイル名 (小文字) → その名前の rel 全部 (Files の並び。同じ名前のファイルが別のフォルダにあるときの候補)
        public Dictionary<string, List<string>> BaseToRels { get; } = new(StringComparer.Ordinal);
        // rel の小文字 → rel (records の下のパスで引くため)
        public Dictionary<string, string> RelByLower { get; } = new(StringComparer.Ordinal);
        // 参照先の rel → 参照元の rel (ordinal で並べたもの)。参照の文字の records の下のパスで参照先を決め、パスで引けない参照だけファイル名で引く
        // (BaseToRel。Rev はファイル名だけで引くので、同じ名前の別のファイル (例 mining/mineableelements/ice_raw.json と
        // tintpalettes/organics/minables/ice_raw.json) を取り違える)
        public Dictionary<string, List<string>> RevByRel { get; } = new(StringComparer.Ordinal);
        // entities/ のレコードのタグ (record 名 → タグの uuid。タグが 1 つ以上のものだけ。調査の t1_enttags.json)。並びは調査と同じ
        public List<(string Record, HashSet<string> Tags)> EntityTags { get; } = new();
        // loadoutPath (NormalizeLoadoutPath で XML のキーと同じ形にしたもの) → それを持つレコード (actor/・loadoutkits/・entities/。調査の lp2rec)
        public Dictionary<string, HashSet<string>> LoadoutPathToRecords { get; } = new(StringComparer.Ordinal);
        // StarMapObject の record 名 → name の訳キー (@ を除いたもの。先頭 3000 文字の最初の "name": "@…")
        public List<(string Record, string Key)> StarMapNameKeys { get; } = new();
        // StarMapObject の record 名 → _RecordValue_.parent の参照先 (records の下の相対パスの小文字。例 starmap/pu/system/pyro/pyro1/pyro1.json) /
        // _RecordValue_.type の _RecordId_ (StarMapObjectType の uuid)
        public Dictionary<string, string> StarMapParentRel { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> StarMapTypeId { get; } = new(StringComparer.Ordinal);
        // StarMapObjectType の uuid → name (starmap/starmapobjecttypes.json の types[]。例 Planet / Moon)
        public Dictionary<string, string> StarMapTypeNames { get; } = new(StringComparer.Ordinal);
        // rel の小文字 → record 名 (参照の文字のパスから引く)
        public Dictionary<string, string> RelLowerToRecord { get; } = new(StringComparer.Ordinal);
        // "LootConfig" / "SpawnDescription_" を含むファイル (rel。並びは Files と同じ)
        public List<string> LootConfigFiles { get; } = new();
        public List<string> SpawnFiles { get; } = new();
        // タグの木: タグの uuid → 親の uuid (根は null) / タグの uuid → パス (tagName を / でつないだもの)
        public Dictionary<string, string?> TagParent { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> TagPath { get; } = new(StringComparer.Ordinal);

        // 参照の文字 (group 1: file:// の後ろのパス全体、group 2: ファイル名)
        private static readonly Regex FileRefRegex = new("file://([^\"]*/([^/\"]+)\\.json)\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex RecordNameRegex = new("\"_RecordName_\": \"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex RecordIdRegex = new("\"_RecordId_\": \"([0-9a-f-]{36})\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex LoadoutPathRegex = new("\"loadoutPath\": \"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex StarMapNameRegex = new("\"name\": \"@([^\"]+)\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private sealed class FileInfoResult
        {
            public string? RecordName;
            public string? Id600;
            public string? Id400;
            public string[] RefBases = [];
            public (string? Rel, string Base)[] RefTargets = [];
            public string[] LoadoutPaths = [];
            public string? StarMapKey;
            public string? StarMapParentRel;
            public string? StarMapTypeId;
            public HashSet<string>? EntityTags;
            public bool HasLootConfig;
            public bool HasSpawn;
        }

        // 小分けを 1 つ読む: 各ファイルを調べて (並列に) 結果を取っておき、JSON は縮めて records.bin に足す。索引は Finish で作る
        public void LoadChunk(string recordsRoot, CancellationToken ct)
        {
            var absList = new List<string>();
            void Walk(string dir)
            {
                foreach (var f in Directory.EnumerateFiles(dir)) absList.Add(f);
                foreach (var sub in Directory.EnumerateDirectories(dir)) Walk(sub);
            }
            Walk(recordsRoot);
            var rels = absList.Select(f => Path.GetRelativePath(recordsRoot, f).Replace('\\', '/')).ToArray();

            var results = new FileInfoResult[absList.Count];
            var packed = new byte[absList.Count][];
            Parallel.For(0, absList.Count, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, i =>
            {
                var rel = rels[i];
                var text = File.ReadAllText(absList[i], Encoding.UTF8);
                packed[i] = Pack(text);
                var res = new FileInfoResult();
                var m = RecordNameRegex.Match(text);
                res.RecordName = m.Success ? m.Groups[1].Value : null;
                var head600 = text.Length > 600 ? text[..600] : text;
                var mi = RecordIdRegex.Match(head600);
                res.Id600 = mi.Success ? mi.Groups[1].Value : null;
                if (rel.StartsWith("actor/", StringComparison.Ordinal) || rel.StartsWith("entities/", StringComparison.Ordinal) || rel.StartsWith("harvestable/", StringComparison.Ordinal))
                {
                    var mi4 = RecordIdRegex.Match(text.Length > 400 ? text[..400] : text);
                    res.Id400 = mi4.Success ? mi4.Groups[1].Value : null;
                }
                var baseName = BaseOf(rel);
                var refs = new HashSet<string>(StringComparer.Ordinal);
                var targets = new HashSet<(string? Rel, string Base)>();
                var relLower = rel.ToLowerInvariant();
                foreach (Match fm in FileRefRegex.Matches(text))
                {
                    var b = fm.Groups[2].Value.ToLowerInvariant();
                    if (b != baseName) refs.Add(b);
                    // 参照先のパス (records の下の小文字)。自分自身への参照は除く (パスが無ければ前と同じくファイル名が自分と同じものを除く)
                    var tr = RecordsRelOfRef("file://" + fm.Groups[1].Value);
                    if (tr != null ? tr != relLower : b != baseName) targets.Add((tr, b));
                }
                res.RefBases = refs.ToArray();
                res.RefTargets = targets.ToArray();
                if (rel.StartsWith("actor/", StringComparison.Ordinal) || rel.StartsWith("loadoutkits/", StringComparison.Ordinal) || rel.StartsWith("entities/", StringComparison.Ordinal))
                    res.LoadoutPaths = LoadoutPathRegex.Matches(text).Select(x => NormalizeLoadoutPath(x.Groups[1].Value)).ToArray();
                if (res.RecordName != null && res.RecordName.StartsWith("StarMapObject.", StringComparison.Ordinal))
                {
                    var sm = StarMapNameRegex.Match(text.Length > 3000 ? text[..3000] : text);
                    if (sm.Success) res.StarMapKey = sm.Groups[1].Value;
                    using var smDoc = JsonDocument.Parse(text);
                    if (smDoc.RootElement.TryGetProperty("_RecordValue_", out var smv) && smv.ValueKind == JsonValueKind.Object)
                    {
                        if (smv.TryGetProperty("parent", out var par) && par.ValueKind == JsonValueKind.String)
                            res.StarMapParentRel = RecordsRelOfRef(par.GetString());
                        if (smv.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.Object
                            && ty.TryGetProperty("_RecordId_", out var tyId) && tyId.ValueKind == JsonValueKind.String)
                            res.StarMapTypeId = tyId.GetString();
                    }
                }
                if (rel.StartsWith("entities/", StringComparison.Ordinal))
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.TryGetProperty("_RecordValue_", out var rv) && rv.ValueKind == JsonValueKind.Object
                        && rv.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
                    {
                        var set = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var t in tags.EnumerateArray())
                            if (t.ValueKind == JsonValueKind.Object && t.TryGetProperty("_RecordId_", out var tid) && tid.ValueKind == JsonValueKind.String)
                                set.Add(tid.GetString()!);
                        if (set.Count > 0) res.EntityTags = set;
                    }
                }
                res.HasLootConfig = text.Contains("\"LootConfig\"", StringComparison.Ordinal);
                res.HasSpawn = text.Contains("SpawnDescription_", StringComparison.Ordinal);
                results[i] = res;
            });

            // 小分けは重ならないように分けてあるが、大文字小文字だけ違う同じ rel が 2 度来たら後のものを残す
            // (全件の書き出しでは、後から書いたファイルが上書きしていた)
            _store.Seek(0, SeekOrigin.End);
            for (int i = 0; i < rels.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (_storeIndex.ContainsKey(rels[i]))
                    _pending.RemoveAll(p => string.Equals(p.Rel, rels[i], StringComparison.OrdinalIgnoreCase));
                _storeIndex[rels[i]] = (_store.Position, packed[i].Length);
                _store.Write(packed[i]);
                _pending.Add((rels[i], results[i]));
            }
            _store.Flush();
        }

        private static byte[] Pack(string text)
        {
            using var ms = new MemoryStream();
            using (var z = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                z.Write(bytes, 0, bytes.Length);
            }
            return ms.ToArray();
        }

        // 全件の木を歩いたとき (前の作り方の Walk・調査の os.walk) の並び: 初めて違う段で、ファイル (その段が最後) はフォルダより先、
        // 同じ種類どうしは名前の順。名前の順は NTFS の並び (大文字にそろえた比べ方) に合わせて OrdinalIgnoreCase で比べる
        private static int WalkOrder(string a, string b)
        {
            var pa = a.Split('/');
            var pb = b.Split('/');
            for (int i = 0; i < Math.Min(pa.Length, pb.Length); i++)
            {
                var c = StringComparer.OrdinalIgnoreCase.Compare(pa[i], pb[i]);
                if (c == 0) continue;
                bool fa = i == pa.Length - 1, fb = i == pb.Length - 1;
                if (fa != fb) return fa ? -1 : 1;
                return c;
            }
            return pa.Length.CompareTo(pb.Length);
        }

        // 全部の小分けを読んだ後に、全件の並びで索引を作る (前の Load の後半と同じ)
        public void Finish()
        {
            var d = this;
            _pending.Sort((x, y) => WalkOrder(x.Rel, y.Rel));
            foreach (var (rel, _) in _pending) d.Files.Add(rel);
            var rev = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            for (int i = 0; i < _pending.Count; i++)
            {
                var rel = d.Files[i];
                var res = _pending[i].Res;
                d.Rec[rel] = res.RecordName;
                foreach (var b in res.RefBases)
                {
                    if (!rev.TryGetValue(b, out var set)) rev[b] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(rel);
                }
                d.BaseToRel[BaseOf(rel)] = rel;
                if (res.HasLootConfig) d.LootConfigFiles.Add(rel);
                if (res.HasSpawn) d.SpawnFiles.Add(rel);
                if (res.RecordName == null) continue;
                var rn = res.RecordName;
                d.RelLowerToRecord[rel.ToLowerInvariant()] = rn;
                if (res.StarMapParentRel != null) d.StarMapParentRel[rn] = res.StarMapParentRel;
                if (res.StarMapTypeId != null) d.StarMapTypeId[rn] = res.StarMapTypeId;
                if (res.Id600 != null) d.IdToRecord[res.Id600] = rn;
                if (res.Id400 != null) d.AleIdToRecord[res.Id400] = rn;
                d.BaseToRecord[BaseOf(rel)] = rn;
                d.RecordToFile[rn] = rel;
                foreach (var lp in res.LoadoutPaths)
                {
                    if (!d.LoadoutPathToRecords.TryGetValue(lp, out var set)) d.LoadoutPathToRecords[lp] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(rn);
                }
                if (res.StarMapKey != null) d.StarMapNameKeys.Add((rn, res.StarMapKey));
                if (res.EntityTags != null) d.EntityTags.Add((rn, res.EntityTags));
            }
            foreach (var (k, v) in rev)
            {
                var list = v.ToList();
                list.Sort(StringComparer.Ordinal);
                d.Rev[k] = list;
            }
            // パスで引く索引 (RelByLower・BaseToRels) と、参照先の rel ごとの参照元 (RevByRel)
            foreach (var rel in d.Files)
            {
                d.RelByLower[rel.ToLowerInvariant()] = rel;
                if (!d.BaseToRels.TryGetValue(BaseOf(rel), out var same)) d.BaseToRels[BaseOf(rel)] = same = new List<string>();
                same.Add(rel);
            }
            var revByRel = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            for (int i = 0; i < _pending.Count; i++)
            {
                var rel = d.Files[i];
                foreach (var (tr, b) in _pending[i].Res.RefTargets)
                {
                    var target = tr != null && d.RelByLower.TryGetValue(tr, out var byPath) ? byPath : d.BaseToRel.TryGetValue(b, out var byBase) ? byBase : null;
                    if (target == null || target == rel) continue;
                    if (!revByRel.TryGetValue(target, out var set)) revByRel[target] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(rel);
                }
            }
            foreach (var (k, v) in revByRel)
            {
                var list = v.ToList();
                list.Sort(StringComparer.Ordinal);
                d.RevByRel[k] = list;
            }

            // タグの木 (tagdatabase/tagdatabase.tagdatabase.json の tags[] の children の入れ子)
            if (ReadText("tagdatabase/tagdatabase.tagdatabase.json") is { } tagText)
            {
                using var doc = JsonDocument.Parse(tagText);
                void WalkTag(JsonElement t, string? parent, string parentPath)
                {
                    if (t.ValueKind != JsonValueKind.Object || !t.TryGetProperty("_RecordId_", out var idEl) || idEl.ValueKind != JsonValueKind.String) return;
                    var id = idEl.GetString()!;
                    var name = t.TryGetProperty("tagName", out var tn) && tn.ValueKind == JsonValueKind.String ? tn.GetString() ?? "" : "";
                    var path = parentPath.Length == 0 && parent == null ? name : parentPath + "/" + name;
                    d.TagParent[id] = parent;
                    d.TagPath[id] = path;
                    if (t.TryGetProperty("children", out var ch) && ch.ValueKind == JsonValueKind.Array)
                        foreach (var c in ch.EnumerateArray()) WalkTag(c, id, path);
                }
                if (doc.RootElement.TryGetProperty("_RecordValue_", out var rv) && rv.ValueKind == JsonValueKind.Object
                    && rv.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
                    foreach (var t in tags.EnumerateArray()) WalkTag(t, null, "");
            }

            // 星図の種類 (starmap/starmapobjecttypes.json の _RecordValue_.types[] の _RecordId_ と name)
            if (ReadText("starmap/starmapobjecttypes.json") is { } smTypeText)
            {
                using var doc = JsonDocument.Parse(smTypeText);
                if (doc.RootElement.TryGetProperty("_RecordValue_", out var rv) && rv.ValueKind == JsonValueKind.Object
                    && rv.TryGetProperty("types", out var types) && types.ValueKind == JsonValueKind.Array)
                    foreach (var t in types.EnumerateArray())
                        if (t.ValueKind == JsonValueKind.Object && t.TryGetProperty("_RecordId_", out var id) && id.ValueKind == JsonValueKind.String
                            && t.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
                            d.StarMapTypeNames[id.GetString()!] = nm.GetString() ?? "";
            }
            _pending.Clear();
        }

        // 参照の文字 ("file://./../../libs/foundry/records/starmap/…/x.json") → records の下の相対パスの小文字 (starmap/…/x.json)。形が違えば null
        private const string RecordsMarker = "libs/foundry/records/";
        internal static string? RecordsRelOfRef(string? reference)
        {
            if (string.IsNullOrEmpty(reference)) return null;
            var s = reference.Replace('\\', '/');
            var i = s.IndexOf(RecordsMarker, StringComparison.OrdinalIgnoreCase);
            return i < 0 ? null : s[(i + RecordsMarker.Length)..].ToLowerInvariant();
        }

        // loadoutPath の JSON の文字 (エスケープのまま) を、ロードアウトの XML のキー (ReadLoadoutItemNames の rel。小文字・/ 区切り) と同じ形にする。
        // DCB には "Scripts\\Loadouts\\…" (JSON の文字では \\ が 2 字。元の値に \ が 2 つ続くものもある) と "Scripts/Loadouts/…" が混じるので、
        // JSON の文字としてほどいてから \ を / にし、続いた / を 1 つにする
        private static readonly Regex RepeatedSlashRegex = new("/{2,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        internal static string NormalizeLoadoutPath(string jsonRaw)
        {
            string s;
            try { s = JsonSerializer.Deserialize<string>("\"" + jsonRaw + "\"") ?? jsonRaw; }
            catch (JsonException) { s = jsonRaw.Replace("\\\\", "\\"); }
            return RepeatedSlashRegex.Replace(s.Replace('\\', '/'), "/").ToLowerInvariant();
        }

        // rel のファイル名 (小文字・.json を除く。調査の rsplit('/')[-1][:-5].lower())
        public static string BaseOf(string rel)
        {
            var name = rel[(rel.LastIndexOf('/') + 1)..];
            return (name.Length >= 5 ? name[..^5] : "").ToLowerInvariant();
        }

        // ロードアウトの XML の itemName (小文字) → その XML (Data の下の相対パスの小文字。例 scripts/loadouts/…/x.xml)。
        // 取り出した XML は CryXmlB (CryXmlParser で読む)。調査は文字の XML の itemName="…" を数えたので、要素の itemName 属性 (空でないもの) を同じに扱う
        public static (Dictionary<string, HashSet<string>> Map, int Files, int Bad) ReadLoadoutItemNames(string dataDir, CancellationToken ct)
        {
            var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            int files = 0, bad = 0;
            if (!Directory.Exists(dataDir)) return (map, 0, 0);
            foreach (var f in Directory.EnumerateFiles(dataDir, "*", SearchOption.AllDirectories))
            {
                if (!f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(dataDir, f).Replace('\\', '/').ToLowerInvariant();
                try
                {
                    var doc = CryXmlParser.Parse(f);
                    files++;
                    foreach (var el in doc.Descendants())
                    {
                        var a = el.Attribute("itemName");
                        if (a == null || a.Value.Length == 0) continue;
                        var key = a.Value.ToLowerInvariant();
                        if (!map.TryGetValue(key, out var set)) map[key] = set = new HashSet<string>(StringComparer.Ordinal);
                        set.Add(rel);
                    }
                }
                catch { bad++; }
            }
            return (map, files, bad);
        }
    }

    // ── socpak の索引 (調査の t10_uuidindex.json・s6_ocgraph.json・s5_planets.json の creatures) ──
    internal sealed class DeliverableSocpaks
    {
        // record 名 → それの uuid が出てくる socpak (ObjectContainers の下の相対パスの小文字)
        public Dictionary<string, HashSet<string>> RecordToSocpaks { get; } = new(StringComparer.Ordinal);
        // 子の socpak → 親の socpak
        public Dictionary<string, HashSet<string>> Parents { get; } = new(StringComparer.Ordinal);
        // (.ale の入っている socpak (ObjectContainers の下の相対パスの小文字), .ale のファイル名 (.ale を除く)) → tokenPairs の guid (record 名に引けたものは record 名)
        public Dictionary<(string Socpak, string Ale), HashSet<string>> AleCreatures { get; } = new();
        // 子の socpak → それを置く Entity (同じ entxml) に書かれた StarMapObject (例 nyxsystem.socpak の entdata/2998839955.entxml の
        // objectContainer="…/keeger_segment_rckcrk_001.socpak" と starmapRecord="36a9e3b4-…" (StarMapObject.AsteroidClusterBase_Nyx_Rockcracker_001))
        public Dictionary<string, HashSet<string>> ChildStarMap { get; } = new(StringComparer.Ordinal);
        // 子の socpak → (それを置く Entity のある親の socpak, その Entity の StarMapObject)。ChildStarMap と同じ組を、どの親の中の組かを残して持つ
        // (資源の採掘できる場所 (FindOreLocations) で、別の星系の親に置かれた組を除くのに使う。ChildStarMap の使い方は変えない)
        public Dictionary<string, HashSet<(string Parent, string Smo)>> ChildStarMapFrom { get; } = new(StringComparer.Ordinal);
        public int SocpakCount { get; private set; }
        public int BadCount { get; private set; }

        private static readonly Regex UuidRegex = new("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        // 調査の s6: rb'[Oo]bject[Cc]ontainers/([A-Za-z0-9_./{}\-]+?\.socpak)' を re.I で (英字だけ大文字小文字を無視)
        private static readonly Regex ChildRegex = new("[Oo][Bb][Jj][Ee][Cc][Tt][Cc][Oo][Nn][Tt][Aa][Ii][Nn][Ee][Rr][Ss]/([A-Za-z0-9_./{}\\-]+?\\.[Ss][Oo][Cc][Pp][Aa][Kk])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private const string OcPrefix = "Data/ObjectContainers/";

        public static DeliverableSocpaks Scan(string p4kPath, DeliverableDcb dcb, CancellationToken ct)
        {
            var s = new DeliverableSocpaks();
            var children = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            using var fs = new FileStream(p4kPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            // 読む (順に) と調べる (並列に) を分ける。socpak は無圧縮で入っているので、entry をそのまま読んで中の zip を開く (一時ファイルは作らない)
            var queue = new System.Collections.Concurrent.BlockingCollection<(string Rel, byte[] Data)>(boundedCapacity: 8);
            var results = new System.Collections.Concurrent.ConcurrentBag<(string Rel, HashSet<string> Records, HashSet<string>? Kids, List<(string Planet, HashSet<string> Creatures)> Ale, List<(string Child, string Smo)> ChildSmo)>();
            int bad = 0;
            var workers = Enumerable.Range(0, Math.Max(1, Environment.ProcessorCount - 1)).Select(_ => Task.Run(() =>
            {
                foreach (var (rel, data) in queue.GetConsumingEnumerable(ct))
                {
                    try { results.Add(ScanOne(rel, data, dcb)); }
                    catch { Interlocked.Increment(ref bad); }
                }
            }, ct)).ToArray();
            int count = 0;
            try
            {
                foreach (var e in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var name = e.FullName.Replace('\\', '/');
                    if (!name.EndsWith(".socpak", StringComparison.OrdinalIgnoreCase) || !name.StartsWith(OcPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    var rel = name[OcPrefix.Length..].ToLowerInvariant();
                    byte[] data;
                    try
                    {
                        using var es = e.Open();
                        using var ms = new MemoryStream((int)Math.Max(0, Math.Min(e.Length, int.MaxValue)));
                        es.CopyTo(ms);
                        data = ms.ToArray();
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { Interlocked.Increment(ref bad); continue; }
                    count++;
                    queue.Add((rel, data), ct);
                }
            }
            finally { queue.CompleteAdding(); }
            try { Task.WaitAll(workers, ct); }
            catch (AggregateException) when (ct.IsCancellationRequested) { ct.ThrowIfCancellationRequested(); }
            s.SocpakCount = count;
            s.BadCount = bad;
            foreach (var (rel, records, kids, ale, childSmo) in results)
            {
                foreach (var (child, smo) in childSmo)
                {
                    if (!s.ChildStarMap.TryGetValue(child, out var cs)) s.ChildStarMap[child] = cs = new HashSet<string>(StringComparer.Ordinal);
                    cs.Add(smo);
                    if (!s.ChildStarMapFrom.TryGetValue(child, out var cf)) s.ChildStarMapFrom[child] = cf = new HashSet<(string Parent, string Smo)>();
                    cf.Add((rel, smo));
                }
                foreach (var rn in records)
                {
                    if (!s.RecordToSocpaks.TryGetValue(rn, out var set)) s.RecordToSocpaks[rn] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(rel);
                }
                if (kids != null) children[rel] = kids;
                foreach (var (planet, creatures) in ale) s.AleCreatures[(rel, planet)] = creatures;
            }
            foreach (var (p, ks) in children)
                foreach (var k in ks)
                {
                    if (!s.Parents.TryGetValue(k, out var set)) s.Parents[k] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(p);
                }
            return s;
        }

        private static (string Rel, HashSet<string> Records, HashSet<string>? Kids, List<(string Planet, HashSet<string> Creatures)> Ale, List<(string Child, string Smo)> ChildSmo) ScanOne(string rel, byte[] data, DeliverableDcb dcb)
        {
            var records = new HashSet<string>(StringComparer.Ordinal);
            var kids = new HashSet<string>(StringComparer.Ordinal);
            var ale = new List<(string, HashSet<string>)>();
            var childSmo = new List<(string, string)>();
            using var inner = new ZipArchive(new MemoryStream(data, writable: false), ZipArchiveMode.Read);
            foreach (var ie in inner.Entries)
            {
                var iname = ie.FullName.Replace('\\', '/');
                if (iname.EndsWith('/')) continue;
                byte[] bytes;
                using (var es = ie.Open())
                using (var ms = new MemoryStream())
                {
                    es.CopyTo(ms);
                    bytes = ms.ToArray();
                }
                var text = Encoding.Latin1.GetString(bytes);
                var lower = iname.ToLowerInvariant();
                var isEntxml = lower.EndsWith(".entxml", StringComparison.Ordinal);
                // entxml の中の StarMapObject の uuid (子の socpak との組に使う)
                List<string>? entSmos = null;
                foreach (Match m in UuidRegex.Matches(text))
                    if (dcb.IdToRecord.TryGetValue(m.Value, out var rn))
                    {
                        records.Add(rn);
                        if (isEntxml && rn.StartsWith("StarMapObject.", StringComparison.Ordinal)) (entSmos ??= new List<string>()).Add(rn);
                    }
                if (lower.EndsWith(".xml", StringComparison.Ordinal) || isEntxml
                    || lower.EndsWith(".soc", StringComparison.Ordinal) || lower.EndsWith(".ale", StringComparison.Ordinal))
                {
                    foreach (Match m in ChildRegex.Matches(text))
                    {
                        var c = m.Groups[1].Value.ToLowerInvariant();
                        if (c != rel) kids.Add(c);
                        // entxml は 1 つが 1 つの Entity (entdata/<番号>.entxml。nyxsystem.socpak の 232 個はどれも <Entity が 1 つ) なので、
                        // 同じ entxml の子の socpak と StarMapObject を組にする
                        if (entSmos != null && c != rel)
                            foreach (var smo in entSmos) childSmo.Add((c, smo));
                    }
                }
                // 惑星などの .ale (pu/system の下の socpak の中の *.ale)。先頭が CrChF で、最初の { からが JSON
                if (rel.StartsWith("pu/system/", StringComparison.Ordinal) && iname.EndsWith(".ale", StringComparison.Ordinal))
                {
                    var fileName = iname[(iname.LastIndexOf('/') + 1)..];
                    var planet = fileName[..^4];
                    var i = Array.IndexOf(bytes, (byte)'{');
                    if (i < 0) continue;
                    try
                    {
                        var json = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes, i, bytes.Length - i));
                        var reader = new Utf8JsonReader(json, new JsonReaderOptions { AllowTrailingCommas = false });
                        using var doc = JsonDocument.ParseValue(ref reader);
                        var creatures = new HashSet<string>(StringComparer.Ordinal);
                        void WalkAle(JsonElement o)
                        {
                            if (o.ValueKind == JsonValueKind.Object)
                            {
                                if (o.TryGetProperty("tokenPairs", out var tps) && tps.ValueKind == JsonValueKind.Array)
                                    foreach (var tp in tps.EnumerateArray())
                                        if (tp.ValueKind == JsonValueKind.Object && tp.TryGetProperty("guid", out var g) && g.ValueKind == JsonValueKind.String)
                                        {
                                            var guid = g.GetString()!;
                                            creatures.Add(dcb.AleIdToRecord.TryGetValue(guid, out var rn) ? rn : guid);
                                        }
                                foreach (var p in o.EnumerateObject()) WalkAle(p.Value);
                            }
                            else if (o.ValueKind == JsonValueKind.Array)
                                foreach (var v in o.EnumerateArray()) WalkAle(v);
                        }
                        WalkAle(doc.RootElement);
                        ale.Add((planet, creatures));
                    }
                    catch (Exception) { }   // 調査と同じく、JSON として読めない .ale は飛ばす (socpak のほかの中身は使う)
                }
            }
            return (rel, records, kids, ale, childSmo);
        }
    }

    // ── 照合 (調査の t11_all.py。ルート表の評価は t2b_eval.py、出現の設定は t8_spawn.py) ──
    internal sealed class DeliverableSourceFinder
    {
        private readonly DeliverableDcb _d;
        private readonly DeliverableSocpaks _s;
        private readonly Dictionary<string, HashSet<string>> _ld;
        private readonly Dictionary<string, string> _ini = new(StringComparer.OrdinalIgnoreCase);
        // StarMapObject の record 名 → 英語名 (引けなければ "@" + キー) / record の短い名前 (小文字) → 英語名
        private readonly Dictionary<string, string> _smoName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _pln = new(StringComparer.Ordinal);
        // socpak → そこに uuid の出る StarMapObject
        private readonly Dictionary<string, HashSet<string>> _socSmo = new(StringComparer.Ordinal);
        // pu/system の下の socpak → そこに出る星図の場所の惑星・月 (短い名前の小文字。_pln のキー)
        private readonly Dictionary<string, HashSet<string>> _socPlanets = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (HashSet<string> Planets, HashSet<string> Near)> _whereCache = new(StringComparer.Ordinal);
        private readonly Dictionary<string, JsonElement?> _refCache = new(StringComparer.Ordinal);
        private readonly List<JsonDocument> _docs = new();

        private static readonly string[] Skip = ["contracts/", "crafting/", "entitlementpolicies/", "ui/", "missionbroker/", "communication", "dialogue",
            "tagdatabase/", "resourcetypedatabase/", "radarsystem/", "actor/playerdefaultactionsconfig"];

        public DeliverableSourceFinder(DeliverableDcb dcb, DeliverableSocpaks soc, Dictionary<string, HashSet<string>> loadoutItems, Dictionary<string, string>? loc)
        {
            _d = dcb; _s = soc; _ld = loadoutItems;
            if (loc != null)
            {
                foreach (var (k, v) in loc) _ini[k.Split(',')[0]] = v.Trim();
            }
            foreach (var (rn, key) in dcb.StarMapNameKeys)
                _smoName[rn] = _ini.TryGetValue(key, out var en) ? en : "@" + key;
            foreach (var (rn, name) in _smoName)
            {
                var dot = rn.IndexOf('.');
                _pln[(dot >= 0 ? rn[(dot + 1)..] : rn).ToLowerInvariant()] = name;
            }
            foreach (var (rn, socs) in soc.RecordToSocpaks)
                if (rn.StartsWith("StarMapObject.", StringComparison.Ordinal))
                    foreach (var x in socs)
                    {
                        if (!_socSmo.TryGetValue(x, out var set)) _socSmo[x] = set = new HashSet<string>(StringComparer.Ordinal);
                        set.Add(rn);
                    }
            // socpak → その中に uuid の出る StarMapObject の、parent を辿って最初に当たる惑星・月 (StarMapObjectType の name が Planet / Moon) の短い名前の小文字。
            // pu/system/<星系>/<名前>.socpak の形でない惑星・月の socpak (例 pu/system/pyro/preludetonyx/pyro1_ptn.socpak) を Where で惑星・月にするのに使う
            var planetTypes = new HashSet<string>(dcb.StarMapTypeNames.Where(kv => kv.Value is "Planet" or "Moon").Select(kv => kv.Key), StringComparer.Ordinal);
            string? PlanetOf(string smo)
            {
                string? cur = smo;
                for (int guard = 0; cur != null && guard < 32; guard++)
                {
                    if (dcb.StarMapTypeId.TryGetValue(cur, out var ty) && planetTypes.Contains(ty)) return Short(cur).ToLowerInvariant();
                    cur = dcb.StarMapParentRel.TryGetValue(cur, out var prel) && dcb.RelLowerToRecord.TryGetValue(prel, out var prn)
                        && prn.StartsWith("StarMapObject.", StringComparison.Ordinal) ? prn : null;
                }
                return null;
            }
            foreach (var (x, smos) in _socSmo)
            {
                if (!x.StartsWith("pu/system/", StringComparison.Ordinal)) continue;
                foreach (var smo in smos)
                {
                    // 惑星・月そのものの uuid は星系の socpak (例 pyrosystem.socpak) に出るので、惑星・月の下の場所 (parent が惑星・月の側) だけを見る
                    if (dcb.StarMapTypeId.TryGetValue(smo, out var ty) && planetTypes.Contains(ty)) continue;
                    if (PlanetOf(smo) is not { } p) continue;
                    if (!_socPlanets.TryGetValue(x, out var set)) _socPlanets[x] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(p);
                }
            }
        }

        // StarMapObject の並び → 使える英語名 (訳の無い "@キー" はそのまま出す。<= UNINITIALIZED => や PLACEHOLDER の訳は除く)
        private IEnumerable<string> Named(IEnumerable<string>? smos)
        {
            if (smos == null) yield break;
            foreach (var smo in smos)
            {
                var n = _smoName.TryGetValue(smo, out var v) ? v : smo;
                if (n.StartsWith('@') || OreService.EnglishText(n) is not null) yield return n;
            }
        }

        // socpak のフォルダ (pu/station/motel) と同じ並びのフォルダ (starmap/pu/station/motel) に記録のファイルがある StarMapObject
        private Dictionary<string, List<string>>? _smoByFolder;
        private List<string> SmoInFolder(string socDir)
        {
            if (_smoByFolder == null)
            {
                _smoByFolder = new(StringComparer.OrdinalIgnoreCase);
                foreach (var (rn, rel) in _d.RecordToFile)
                {
                    if (!rn.StartsWith("StarMapObject.", StringComparison.Ordinal)) continue;
                    var slash = rel.LastIndexOf('/');
                    if (slash < 0) continue;
                    var dir = rel[..slash];
                    if (!_smoByFolder.TryGetValue(dir, out var list)) _smoByFolder[dir] = list = new List<string>();
                    list.Add(rn);
                }
            }
            return _smoByFolder.TryGetValue("starmap/" + socDir, out var hit) ? hit : [];
        }

        // *_planet_only.socpak の置かれたフォルダの名前 →星図の記録の名前の後ろの部分 (Stanton3_Area18 の area18)。フォルダの名前のままで引けないもの
        private static readonly Dictionary<string, string> PlanetOnlyFolderAlias = new(StringComparer.OrdinalIgnoreCase)
        {
            ["a18"] = "area18",
        };

        // <星>_planet_only.socpak / <…>_planetonly.socpak の星 (_pln のキー)。名前の頭を下線で区切り、下線を除いて先頭からつないだもののうち
        // 星図の記録に当たるいちばん長いもの (stanton1 → Hurston、stanton_2c_yela → stanton2c = Yela)。当たらなければ null
        private string? PlanetOnlyBody(string soc)
        {
            var fileName = soc[(soc.LastIndexOf('/') + 1)..];
            string head;
            if (fileName.EndsWith("_planet_only.socpak", StringComparison.Ordinal)) head = fileName[..^"_planet_only.socpak".Length];
            else if (fileName.EndsWith("_planetonly.socpak", StringComparison.Ordinal)) head = fileName[..^"_planetonly.socpak".Length];
            else return null;
            string? found = null;
            var acc = "";
            foreach (var t in head.Split('_'))
            {
                acc += t;
                if (_pln.ContainsKey(acc)) found = acc;
            }
            return found;
        }

        private static string Short(string rn) { var i = rn.IndexOf('.'); return i >= 0 ? rn[(i + 1)..] : rn; }
        private static string TypeOf(string rn) { var i = rn.IndexOf('.'); return i >= 0 ? rn[..i] : rn; }

        // socpak → (惑星・月の socpak の名前, いちばん近い名前付きの場所の名前)。親を幅優先で辿る (調査の where)
        private (HashSet<string> Planets, HashSet<string> Near) Where(string soc)
        {
            soc = soc.ToLowerInvariant();
            if (_whereCache.TryGetValue(soc, out var hit)) return hit;
            var pl = new HashSet<string>(StringComparer.Ordinal);
            var near = new HashSet<string>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal) { soc };
            var fr = new Queue<(string X, int Dp)>();
            fr.Enqueue((soc, 0));
            int best = 99;
            while (fr.Count > 0)
            {
                var (x, dp) = fr.Dequeue();
                // <星>_planet_only.socpak は、その星の部分だけの socpak (例 pu/loc/flagship/stanton/lorville/stanton1_planet_only.socpak
                // = Lorville の Hurston、pu/system/stanton/stanton4_planet_only.socpak = microTech)。星は星図の記録 (stanton1 → Hurston)、
                // 置かれたフォルダの名前と星の名前を合わせた星図の記録 (Stanton1_Lorville = Lorville) があれば、それを近い場所にする
                if (PlanetOnlyBody(x) is { } body)
                {
                    pl.Add(body);
                    // 「/」の前のフォルダを場所にする (lorville/stanton1_planet_only → Lorville、a18/stanton3_planet_only → Area18)。
                    // 名前は「<星>_<フォルダ>」の星図の記録 (Stanton1_Lorville) の英語名。フォルダの名前が星図の記録と違うものは
                    // PlanetOnlyFolderAlias で置き換える (a18 → area18 = Stanton3_Area18)。引けなければフォルダの名前のまま。
                    // フォルダが星系 (pu/system/stanton) のものは星だけ
                    var parts = x.Split('/');
                    if (parts.Length >= 2 && dp <= best && !(parts.Length == 4 && parts[0] == "pu" && parts[1] == "system"))
                    {
                        var folder = parts[^2];
                        var key = PlanetOnlyFolderAlias.TryGetValue(folder, out var alias) ? alias : folder;
                        best = dp;
                        // 引けなければ、同じフォルダの並びの星図の記録 (pu/station/motel → starmap/pu/station/motel/starmapobject.grimhex.json
                        // = GrimHEX) の英語名。それも無ければフォルダの名前のまま
                        if (_pln.TryGetValue(body + "_" + key, out var placeName)) near.Add(placeName);
                        else if (Named(SmoInFolder(x[..x.LastIndexOf('/')])).ToList() is { Count: > 0 } folderNames) near.UnionWith(folderNames);
                        else near.Add(folder);
                    }
                }
                else if (x.StartsWith("pu/system/", StringComparison.Ordinal) && x.Count(ch => ch == '/') == 3)
                {
                    var name = x[(x.LastIndexOf('/') + 1)..];
                    name = name.Length >= 7 ? name[..^7] : "";
                    // 星図の記録の名前が「<星系>_<socpak の名前>」のもの (例 akirocluster.socpak → StarMapObject.Pyro_AkiroCluster = Akiro Cluster)
                    var sys = x.Split('/')[2];
                    if (!_pln.ContainsKey(name) && _pln.ContainsKey(sys + "_" + name)) name = sys + "_" + name;
                    pl.Add(name);
                }
                else if (_socPlanets.TryGetValue(x, out var socPl))
                    pl.UnionWith(socPl);
                else if (dp <= best
                    && (Named(!x.StartsWith("pu/system/", StringComparison.Ordinal) && _socSmo.TryGetValue(x, out var smos) ? smos : null)
                        .Concat(Named(_s.ChildStarMap.TryGetValue(x, out var childSmos) ? childSmos : null)).ToList() is { Count: > 0 } names))
                {
                    // 名前付きの場所: その socpak に uuid の出る StarMapObject (pu/system の下は除く) と、その socpak を置く Entity の StarMapObject。
                    // 名前の訳が入っていない (<= UNINITIALIZED => など) StarMapObject は名前付きの場所に数えない
                    best = dp;
                    near.UnionWith(names);
                }
                if (_s.Parents.TryGetValue(x, out var ps))
                    foreach (var p in ps)
                        if (seen.Add(p)) fr.Enqueue((p, dp + 1));
            }
            return _whereCache[soc] = (pl, near);
        }

        // 品 (資源は MineableElement) を参照するレコードを上へ辿る (調査の upchain。6 段まで、契約・UI などの参照は辿らない)。
        // 辿るのは rel (records の下のパス) で、参照元は RevByRel (参照の文字のパスで決めたもの。ファイル名だけでは引かない)。
        // Path は辿った rel の並び (先頭が出発の rel)。画面の via の文字はファイル名 (PathBases) にする
        private List<(string? Rec, List<string> Path)> UpChain(IEnumerable<string> startRels, int maxd = 6)
        {
            var result = new List<(string?, List<string>)>();
            var starts = startRels.Distinct(StringComparer.Ordinal).ToList();
            var seen = new HashSet<string>(starts, StringComparer.Ordinal);
            var fr = new Queue<(string X, int Dp, List<string> Path)>();
            foreach (var st in starts) fr.Enqueue((st, 0, new List<string> { st }));
            while (fr.Count > 0)
            {
                var (x, dp, path) = fr.Dequeue();
                if (!_d.RevByRel.TryGetValue(x, out var rs)) continue;
                foreach (var r in rs)
                {
                    if (Skip.Any(p => r.StartsWith(p, StringComparison.Ordinal))) continue;
                    if (seen.Contains(r) || dp >= maxd) continue;
                    seen.Add(r);
                    var np = new List<string>(path) { r };
                    result.Add((_d.Rec.TryGetValue(r, out var rn) ? rn : null, np));
                    fr.Enqueue((r, dp + 1, np));
                }
            }
            return result;
        }

        // 辿った rel の並び → ファイル名 (小文字・.json を除く) の並び (via の文字・ロードアウトの itemName との照合に使う)
        private static List<string> PathBases(IEnumerable<string> rels) => rels.Select(DeliverableDcb.BaseOf).ToList();

        // 品 (エンティティのファイル名の小文字) の rel。契約の納品物はエンティティ (EntityClassDefinition) のファイル名しか持たないので、
        // その名前のファイルのうち EntityClassDefinition のもの (同じ名前の別の種類のファイルを取り違えないように)。無ければ前と同じく BaseToRel (後に出たもの)
        private List<string> EntityTargetRels(string k)
        {
            var list = _d.BaseToRels.TryGetValue(k, out var same)
                ? same.Where(r => _d.Rec.TryGetValue(r, out var rn) && rn != null && rn.StartsWith("EntityClassDefinition.", StringComparison.Ordinal)).ToList()
                : new List<string>();
            if (list.Count == 0 && _d.BaseToRel.TryGetValue(k, out var one)) list.Add(one);
            return list;
        }

        // ロードアウトの XML を使う actor (SLoadoutAssortment は、それを参照する EntityClassDefinition に置き換える。調査の actors_for_xml)
        private HashSet<string> ActorsForXml(string x)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (!_d.LoadoutPathToRecords.TryGetValue(x, out var recs)) return result;
            foreach (var rn in recs)
            {
                if (rn.StartsWith("SLoadoutAssortment", StringComparison.Ordinal))
                {
                    if (!_d.RecordToFile.TryGetValue(rn, out var f)) continue;
                    if (!_d.RevByRel.TryGetValue(f, out var rs)) continue;
                    foreach (var r in rs)
                        if (_d.Rec.TryGetValue(r, out var rr) && rr != null && rr.StartsWith("EntityClassDefinition", StringComparison.Ordinal)) result.Add(rr);
                }
                else result.Add(rn);
            }
            return result;
        }

        private sealed record Src(string Kind, string Via, string? Soc, string Section);

        public List<DeliverableSourceRow> Find(List<string> entities, List<string> resources, CancellationToken ct)
        {
            var rows = new List<DeliverableSourceRow>();
            try
            {
                var loot = EvaluateLoot(entities, ct);
                var spawn = LoadSpawnDescriptions(ct);
                var spawnCache = new Dictionary<string, List<(string Record, string Kind)>>(StringComparer.Ordinal);
                foreach (var it in entities)
                {
                    ct.ThrowIfCancellationRequested();
                    var src = EntitySources(it, loot, spawn, spawnCache);
                    Aggregate("entity", it.ToLowerInvariant(), src, rows);
                }
                foreach (var res in resources)
                {
                    ct.ThrowIfCancellationRequested();
                    Aggregate("resource", res.ToLowerInvariant(), ResourceSources(res), rows);
                }
            }
            finally
            {
                foreach (var doc in _docs) doc.Dispose();
                _docs.Clear();
            }
            return rows;
        }

        // 1 つの品の出所 (調査の t11_all.py の D・A・B・C)
        private List<Src> EntitySources(string it, Dictionary<string, List<(string Owner, string Table)>> loot,
            List<(string Record, string Kind, List<string?> Pos, List<string?> Neg)> spawn, Dictionary<string, List<(string Record, string Kind)>> spawnCache)
        {
            var k = it.ToLowerInvariant();
            var src = new List<Src>();
            // D 品そのものの配置
            var targetRels = EntityTargetRels(k);
            foreach (var tRel in targetRels)
                if (_d.Rec.TryGetValue(tRel, out var rn) && rn != null && _s.RecordToSocpaks.TryGetValue(rn, out var own))
                    foreach (var s in own) src.Add(new Src("配置", Short(rn), s, DeliverableSourceSections.Placed));
            // A DCB を上へ
            var chain = UpChain(targetRels);
            foreach (var (r, path) in chain)
            {
                if (r == null || r.StartsWith("StarMapObject", StringComparison.Ordinal) || r.StartsWith("BuildingBlocks", StringComparison.Ordinal)) continue;
                if (!_s.RecordToSocpaks.TryGetValue(r, out var socs)) continue;
                var section = ChainSection(r, path);
                foreach (var s in socs) src.Add(new Src("参照:" + TypeOf(r), string.Join(" > ", PathBases(path.Skip(1))), s, section));
            }
            // B 生き物 (ロードアウト)
            var lkeys = new List<string> { k };
            foreach (var (r, path) in chain)
                if (r != null && r.StartsWith("EntityClassDefinition", StringComparison.Ordinal)) lkeys.Add(DeliverableDcb.BaseOf(path[^1]));
            var xmls = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kk in lkeys)
                if (_ld.TryGetValue(kk, out var xs)) xmls.UnionWith(xs);
            foreach (var x in xmls)
            {
                var acts = ActorsForXml(x);
                var xmlName = x[(x.LastIndexOf('/') + 1)..];
                if (acts.Count == 0) src.Add(new Src("ロードアウト(持ち主不明)", x, null, DeliverableSourceSections.Holder));
                foreach (var a in acts)
                {
                    var an = Short(a);
                    var located = false;
                    // .ale の入っている socpak そのもの (pu/system/<星系>/…) を場所にする (星系は socpak のパスのとおり)
                    foreach (var ((aleSoc, _), creatures) in _s.AleCreatures)
                    {
                        if (!creatures.Contains(a)) continue;
                        located = true;
                        src.Add(new Src("生態", an + " @" + xmlName, aleSoc, DeliverableSourceSections.Creature));
                    }
                    if (_s.RecordToSocpaks.TryGetValue(a, out var asocs))
                        foreach (var s in asocs) { located = true; src.Add(new Src("ロードアウトの持ち主の配置", an, s, DeliverableSourceSections.Holder)); }
                    if (!spawnCache.TryGetValue(a, out var spawnRows)) spawnCache[a] = spawnRows = SpawnRowsFor(a, spawn);
                    foreach (var (mlt, kind) in spawnRows)
                    {
                        if (kind != "Entity") continue;
                        located = true;
                        if (_s.RecordToSocpaks.TryGetValue(mlt, out var msocs))
                            foreach (var s in msocs) src.Add(new Src("出現:" + TypeOf(mlt), an + " <- " + Short(mlt), s, DeliverableSourceSections.Spawn));
                        else
                            src.Add(new Src("出現(場所不明):" + TypeOf(mlt), an + " <- " + Short(mlt) + " " + SystemTagsText(mlt), null, DeliverableSourceSections.Spawn));
                    }
                    if (!located) src.Add(new Src("ロードアウトの持ち主(場所不明)", an + " @" + xmlName, null, DeliverableSourceSections.Holder));
                }
            }
            // C ルート
            if (loot.TryGetValue(it, out var lootHits))
            {
                foreach (var (owner, table) in lootHits)
                {
                    var owners = new List<string> { owner };
                    if (owner.StartsWith("SubHarvestableConfigRecord", StringComparison.Ordinal) && _d.RecordToFile.TryGetValue(owner, out var f)
                        && _d.RevByRel.TryGetValue(f, out var rs))
                        foreach (var r in rs)
                            if (_d.Rec.TryGetValue(r, out var rr) && rr != null && rr.StartsWith("SubHarvestable", StringComparison.Ordinal) && !owners.Contains(rr)) owners.Add(rr);
                    foreach (var o in owners)
                    {
                        if (_s.RecordToSocpaks.TryGetValue(o, out var socs))
                            foreach (var s in socs) src.Add(new Src("ルート", Short(o) + " / " + table, s, DeliverableSourceSections.Loot));
                        else if (o.StartsWith("EntityClassDefinition", StringComparison.Ordinal))
                            src.Add(new Src("ルート(持ち主の配置不明)", Short(o) + " / " + table, null, DeliverableSourceSections.Loot));
                    }
                }
            }
            return src;
        }

        // 参照の鎖の節: 鎖 (辿ったレコード) にルート表 (型が LootTable で始まる) があればルート (表に載っている)、
        // 採掘・収穫のレコード (型が Harvestable / SubHarvestable / MineableElement で始まる) があれば採掘・収穫、ほかは配置
        private string ChainSection(string r, List<string> path)
        {
            var types = new List<string> { TypeOf(r) };
            // path は辿った rel の並び (UpChain)。その rel の record で見る (ファイル名で引くと同じ名前の別のファイルの record になるため)
            foreach (var pr in path.Skip(1))
                if (_d.Rec.TryGetValue(pr, out var prn) && prn != null) types.Add(TypeOf(prn));
            if (types.Any(t => t.StartsWith("LootTable", StringComparison.Ordinal))) return DeliverableSourceSections.LootListed;
            if (types.Any(t => t.StartsWith("Harvestable", StringComparison.Ordinal) || t.StartsWith("SubHarvestable", StringComparison.Ordinal)))
                return DeliverableSourceSections.Mining;
            return DeliverableSourceSections.Placed;
        }

        // 資源: その ResourceType と、精錬後 (refinedVersion) がそれになる ResourceType を resourceType に持つ MineableElement から上へ辿る (A と同じ)
        private List<Src> ResourceSources(string resourceRecord)
        {
            var src = new List<Src>();
            if (_d.ReadText("resourcetypedatabase/resourcetypedatabase.json") is not { } rtText) return src;
            string? targetId = null;
            var refinedOf = new List<(string Id, string? RefinedId)>();
            using (var doc = JsonDocument.Parse(rtText))
            {
                void Walk(JsonElement o)
                {
                    if (o.ValueKind == JsonValueKind.Object)
                    {
                        if (Str(o, "_Type_") == "ResourceType")
                        {
                            var id = Str(o, "_RecordId_");
                            if (string.Equals(Str(o, "_RecordName_"), resourceRecord, StringComparison.OrdinalIgnoreCase) && id.Length > 0) targetId ??= id;
                            string? refined = o.TryGetProperty("refinedVersion", out var rv) && rv.ValueKind == JsonValueKind.Object ? Str(rv, "_RecordId_") : null;
                            if (id.Length > 0) refinedOf.Add((id, refined));
                        }
                        foreach (var p in o.EnumerateObject()) Walk(p.Value);
                    }
                    else if (o.ValueKind == JsonValueKind.Array)
                        foreach (var v in o.EnumerateArray()) Walk(v);
                }
                Walk(doc.RootElement);
            }
            if (targetId == null) return src;
            var ids = new HashSet<string>(StringComparer.Ordinal) { targetId };
            foreach (var (id, refined) in refinedOf) if (refined == targetId) ids.Add(id);
            foreach (var rel in _d.Files)
            {
                if (_d.Rec.TryGetValue(rel, out var rn) is false || rn == null || !rn.StartsWith("MineableElement.", StringComparison.Ordinal)) continue;
                using var me = JsonDocument.Parse(_d.ReadText(rel)!);
                if (!me.RootElement.TryGetProperty("_RecordValue_", out var mv) || mv.ValueKind != JsonValueKind.Object) continue;
                if (!mv.TryGetProperty("resourceType", out var rt) || rt.ValueKind != JsonValueKind.Object || !ids.Contains(Str(rt, "_RecordId_"))) continue;
                foreach (var (r, path) in UpChain([rel]))
                {
                    if (r == null || r.StartsWith("StarMapObject", StringComparison.Ordinal) || r.StartsWith("BuildingBlocks", StringComparison.Ordinal)) continue;
                    if (!_s.RecordToSocpaks.TryGetValue(r, out var socs)) continue;
                    foreach (var s in socs)
                        src.Add(new Src("参照:" + TypeOf(r), Short(rn) + " > " + string.Join(" > ", PathBases(path.Skip(1))), s, DeliverableSourceSections.Mining));
                }
            }
            return src;
        }

        // 出所を場所にまとめて行にする (調査の t11 の最後の集計。惑星・近い場所は種類ごと、場所まで辿れないものはその文字)
        private void Aggregate(string targetKind, string target, List<Src> src, List<DeliverableSourceRow> rows)
        {
            var seen = new HashSet<(string, string, string, string)>();
            void Add(string section, string kind, string placeType, string name)
            {
                if (seen.Add((section, kind, placeType, name))) rows.Add(new DeliverableSourceRow(targetKind, target, section, kind, placeType, name));
            }
            foreach (var x in src)
            {
                if (x.Soc == null) { Add(x.Section, x.Kind, "unlocated", x.Via); continue; }
                var (pl, near) = Where(x.Soc);
                if (pl.Count == 0 && near.Count == 0) Add(x.Section, x.Kind, "unlocated", x.Via + " [" + x.Soc + "]");
                foreach (var p in pl)
                {
                    if (_pln.TryGetValue(p, out var en)) Add(x.Section, x.Kind, "planet", en);
                    // 星図の名前で引けないもの (nyxsystem など) は、socpak の名前を翻訳のキーとして引く (大文字小文字を無視。例 NyxSystem → Nyx System)
                    else if (_ini.TryGetValue(p, out var iniEn)) Add(x.Section, x.Kind, "planet", iniEn);
                    else Add(x.Section, x.Kind, "planet_raw", p);
                }
                foreach (var n in near) Add(x.Section, x.Kind, "near", n);
            }
        }

        // ── 資源の採掘できる場所 (ore_locations) ──

        // 全 HarvestableProviderPreset の SpaceShip_Mineables / GroundVehicle_Mineables / FPS_Mineables のグループから、
        // (資源, 場所, 掘り方, 岩の種類) の行を作る。辿り方: HarvestableElement の harvestable (HarvestablePreset) の entityClass
        // (harvestable が空なら harvestableEntityClass) → そのエンティティの MineableParams の composition (MineableComposition) →
        // compositionArray[] の mineableElement (MineableElement) の resourceType。辿れない要素は行を作らない (relativeProbability の合計には入れる)
        public List<OreLocationRow> FindOreLocations(CancellationToken ct)
        {
            var rows = new List<OreLocationRow>();
            var compCache = new Dictionary<string, OreComposition?>(StringComparer.Ordinal);
            foreach (var rel in _d.Files)
            {
                if (!_d.Rec.TryGetValue(rel, out var hpp) || hpp == null || !hpp.StartsWith("HarvestableProviderPreset.", StringComparison.Ordinal)) continue;
                ct.ThrowIfCancellationRequested();
                if (_d.ReadText(rel) is not { } text) continue;
                // (資源の uuid, 掘り方, 岩の種類) → 集計
                var agg = new Dictionary<(string Uuid, string Method, string Rock), OreAcc>();
                using (var doc = JsonDocument.Parse(text))
                {
                    if (!doc.RootElement.TryGetProperty("_RecordValue_", out var rv) || rv.ValueKind != JsonValueKind.Object) continue;
                    foreach (var g in Arr(Get(rv, "harvestableGroups")))
                    {
                        var groupName = Str(g, "groupName");
                        var method = OreGroups.FirstOrDefault(x => x.Group == groupName).Method;
                        if (method == null) continue;
                        var hs = Arr(Get(g, "harvestables"));
                        var sum = hs.Sum(h => Num(Get(h, "relativeProbability")));
                        if (sum <= 0) continue;
                        var groupProbability = Num(Get(g, "groupProbability"));
                        foreach (var h in hs)
                        {
                            var rp = Num(Get(h, "relativeProbability"));
                            if (rp <= 0) continue;
                            if (CompositionOf(h, compCache) is not { } comp) continue;
                            var chance = rp / sum * 100;
                            foreach (var part in comp.Parts)
                            {
                                var key = (part.Uuid, method, comp.Rock);
                                if (!agg.TryGetValue(key, out var a))
                                    agg[key] = a = new OreAcc { Record = part.Record, Min = part.Min, Max = part.Max, Prob = part.Prob, GroupProbability = groupProbability };
                                else
                                {
                                    a.Min = Math.Min(a.Min, part.Min);
                                    a.Max = Math.Max(a.Max, part.Max);
                                    a.Prob = Math.Max(a.Prob, part.Prob);
                                }
                                a.Chance += chance;
                            }
                        }
                    }
                }
                if (agg.Count == 0) continue;
                foreach (var (place, system) in OrePlaces(hpp, rel))
                    foreach (var ((uuid, method, rock), a) in agg)
                        rows.Add(new OreLocationRow(uuid, a.Record, hpp, place, system, method, rock, a.Chance, a.Min, a.Max, a.Prob, a.GroupProbability));
            }
            return rows;
        }

        private sealed class OreAcc
        {
            public string Record = "";
            public double Chance, Min, Max, Prob, GroupProbability;
        }

        // 岩の種類 1 つ: 名前と、中の資源 (同じ資源の行はまとめたもの)
        private sealed record OreComposition(string Rock, List<(string Uuid, string Record, double Min, double Max, double Prob)> Parts);

        // 参照の文字 (file://…/libs/foundry/records/…/x.json) のレコードの JSON の文字。records の下のパスで引き、引けなければファイル名で引く
        // (ファイル名だけで引くと、同じ名前のファイルが別のフォルダにあるとき (例 mining/mineableelements/ice_raw.json) 違うレコードになるため)。
        // どちらでも引けなければ null
        private string? RefText(string? reference)
        {
            if (string.IsNullOrEmpty(reference)) return null;
            if (DeliverableDcb.RecordsRelOfRef(reference) is { } relPath && _d.ReadText(relPath) is { } byPath) return byPath;
            var m = RefTailRegex.Match(reference);
            if (!m.Success) return null;
            return _d.BaseToRel.TryGetValue(m.Groups[1].Value.ToLowerInvariant(), out var rel) ? _d.ReadText(rel) : null;
        }

        private static string? RefString(JsonElement o, string key) =>
            o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && (v.GetString() ?? "").Length > 0 ? v.GetString() : null;

        // HarvestableElement → 岩の種類 (辿れなければ null)。MineableComposition の参照の文字で覚える
        private OreComposition? CompositionOf(JsonElement h, Dictionary<string, OreComposition?> cache)
        {
            string? entityRef = null;
            if (RefString(h, "harvestable") is { } presetRef)
            {
                if (RefText(presetRef) is not { } presetText) return null;
                using var pd = JsonDocument.Parse(presetText);
                if (pd.RootElement.TryGetProperty("_RecordValue_", out var pv)) entityRef = RefString(pv, "entityClass");
            }
            else entityRef = RefString(h, "harvestableEntityClass");
            if (entityRef == null || RefText(entityRef) is not { } entityText) return null;
            string? compRef = null;
            using (var ed = JsonDocument.Parse(entityText))
            {
                void Find(JsonElement o)
                {
                    if (compRef != null) return;
                    if (o.ValueKind == JsonValueKind.Object)
                    {
                        if (Str(o, "_Type_") == "MineableParams" && RefString(o, "composition") is { } c) { compRef = c; return; }
                        foreach (var p in o.EnumerateObject()) Find(p.Value);
                    }
                    else if (o.ValueKind == JsonValueKind.Array)
                        foreach (var v in o.EnumerateArray()) Find(v);
                }
                Find(ed.RootElement);
            }
            if (compRef == null) return null;
            if (cache.TryGetValue(compRef, out var hit)) return hit;
            OreComposition? result = null;
            if (RefText(compRef) is { } compText)
            {
                using var cd = JsonDocument.Parse(compText);
                var root = cd.RootElement;
                var compRecord = Str(root, "_RecordName_");
                if (root.TryGetProperty("_RecordValue_", out var cv) && cv.ValueKind == JsonValueKind.Object)
                {
                    var deposit = Str(cv, "depositName");
                    var key = deposit.StartsWith('@') ? deposit[1..] : deposit;
                    var rock = key.Length > 0 && _ini.TryGetValue(key, out var en) && OreService.EnglishText(en) is { } usable ? usable : compRecord;
                    var parts = new List<(string Uuid, string Record, double Min, double Max, double Prob)>();
                    foreach (var part in Arr(Get(cv, "compositionArray")))
                    {
                        if (RefText(RefString(part, "mineableElement")) is not { } meText) continue;
                        using var md = JsonDocument.Parse(meText);
                        if (!md.RootElement.TryGetProperty("_RecordValue_", out var mv) || Get(mv, "resourceType") is not { ValueKind: JsonValueKind.Object } rt) continue;
                        var uuid = Str(rt, "_RecordId_");
                        if (uuid.Length == 0) continue;
                        var record = Str(rt, "_RecordName_");
                        double mn = Num(Get(part, "minPercentage")), mx = Num(Get(part, "maxPercentage")), pr = Num(Get(part, "probability"));
                        var i = parts.FindIndex(x => x.Uuid == uuid);
                        if (i < 0) parts.Add((uuid, record, mn, mx, pr));
                        else parts[i] = (uuid, parts[i].Record, Math.Min(parts[i].Min, mn), Math.Max(parts[i].Max, mx), Math.Max(parts[i].Prob, pr));
                    }
                    if (parts.Count > 0) result = new OreComposition(rock, parts);
                }
            }
            return cache[compRef] = result;
        }

        // 星系だけしか名前の無い小惑星帯の socpak のフォルダ → 場所の名前の訳キー (英語ローカライズで引く)。
        // Glaciem Ring / Keeger Belt の preset (HPP_Nyx_GlaciemRing / HPP_Nyx_KeegerBelt) は、そのフォルダの *_gascloud_gen.socpak に置かれ、
        // 親を辿ると星系 (Nyx System) か、帯の中の駅 (QV Breaker Station …) にしか当たらないため、帯の名前 (global.ini の Nyx_AsteroidBelt1 / 2) を使う
        private static readonly (string Folder, string Key)[] BeltFolderNameKeys =
        [
            ("pu/system/nyx/glaciemring/", "Nyx_AsteroidBelt1"),   // Glaciem Ring
            ("pu/system/nyx/keegerbelt/", "Nyx_AsteroidBelt2"),    // Keeger Belt
        ];

        // HarvestableProviderPreset の置かれた場所 (英語名) と星系。preset の uuid が出てくる socpak ごとに、その socpak 自身を基準に決める:
        //   ・socpak (とその親) のパスの pu/system/<星系>/ の星系 (SocpakSystems) に、preset の星系 (preset のファイルのパスの
        //     harvestable/providerpresets/system/<星系>/) が無いものは使わない (別の星系・テスト用の socpak の使い回し。例 ellis3.socpak・pyro6.socpak の HPP_Stanton2c_Belt)。
        //     その socpak を置く Entity の名前も、親がこの星系の socpak のものだけを使う
        //   ・小惑星帯のフォルダ (BeltFolderNameKeys) の socpak は帯の名前
        //   ・星・月の socpak (pu/system/<星系>/<名前>.socpak。<星>_planet_only は星) はその天体の名前だけ (親の星系・惑星の名前は付けない)
        //   ・ほか (childcloud の .soc など) は、socpak 自身から親を幅優先で辿り、最初に名前 (StarMapObject) が引ける段の名前
        //     (Where の名前付きの場所と同じ引き方。惑星・月の下の場所の socpak はその惑星・月、星・月の socpak はその天体)
        //   ・星系そのものの名前 (Stanton / Nyx System など。星系の socpak・恒星の socpak) は場所に使わない
        // 場所が 1 つも見つからなければ「場所不明 (<preset の record 名の後ろ>)」を場所にする。星系の列は preset の星系
        private List<(string Place, string System)> OrePlaces(string hpp, string hppRel)
        {
            var result = new List<(string, string)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var presetSystem = SystemFromPath(hppRel, "harvestable/providerpresets/system/");
            if (_s.RecordToSocpaks.TryGetValue(hpp, out var socs))
            {
                foreach (var soc in socs.OrderBy(x => x, StringComparer.Ordinal))
                {
                    if (!SocpakSystems(soc).Contains(presetSystem)) continue;
                    foreach (var n in OreSocpakPlaceNames(soc, presetSystem))
                        if (seen.Add(n)) result.Add((n, presetSystem));
                }
            }
            if (result.Count == 0) result.Add(($"場所不明 ({Short(hpp)})", presetSystem));
            return result;
        }

        // 1 つの socpak の場所の名前 (OrePlaces の決め方。並びは名前の順)
        private List<string> OreSocpakPlaceNames(string soc, string system)
        {
            foreach (var (folder, key) in BeltFolderNameKeys)
                if (soc.StartsWith(folder, StringComparison.Ordinal) && _ini.TryGetValue(key, out var belt) && OreService.EnglishText(belt) is { } beltName)
                    return [beltName];
            if (PlanetSocpakName(soc, system) is { } own) return own.Length > 0 ? [own] : [];
            var seen = new HashSet<string>(StringComparer.Ordinal) { soc };
            var level = new List<string> { soc };
            for (int depth = 0; level.Count > 0 && depth < 32; depth++)
            {
                var names = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var x in level)
                {
                    if (depth > 0 && PlanetSocpakName(x, system) is { } planet) { if (planet.Length > 0) names.Add(planet); continue; }
                    // その socpak を置く Entity の StarMapObject は、親の socpak がこの星系のものだけ (同じ socpak が別の星系にも置かれているため。例 miningasteroid_003 は Nyx の delamar にも置かれる)
                    var childSmos = _s.ChildStarMapFrom.TryGetValue(x, out var from)
                        ? from.Where(f => SocpakSystems(f.Parent).Contains(system)).Select(f => f.Smo).Distinct(StringComparer.Ordinal).ToList() : null;
                    foreach (var n in Named(!x.StartsWith("pu/system/", StringComparison.Ordinal) && _socSmo.TryGetValue(x, out var smos) ? smos : null)
                                 .Concat(Named(childSmos)))
                        if (!IsSystemName(n, system)) names.Add(n);
                    if (_socPlanets.TryGetValue(x, out var socPl))
                        foreach (var p in socPl)
                            if (PlanetKeyName(p) is { } pn && !IsSystemName(pn, system)) names.Add(pn);
                }
                if (names.Count > 0) return names.ToList();
                var next = new List<string>();
                foreach (var x in level)
                    if (_s.Parents.TryGetValue(x, out var ps))
                        foreach (var p in ps.OrderBy(v => v, StringComparer.Ordinal))
                            if (seen.Add(p)) next.Add(p);
                level = next;
            }
            return [];
        }

        // 星・月の socpak (pu/system/<星系>/<名前>.socpak。Where の惑星・月の決め方と同じキー) ならその天体の英語名。
        // 星系そのもの・恒星など星系の名前になるもの・名前の引けないものは ""。星・月の socpak でなければ null
        private string? PlanetSocpakName(string soc, string system)
        {
            string key;
            if (PlanetOnlyBody(soc) is { } body) key = body;
            else if (soc.StartsWith("pu/system/", StringComparison.Ordinal) && soc.Count(ch => ch == '/') == 3)
            {
                var name = soc[(soc.LastIndexOf('/') + 1)..];
                name = name.Length >= 7 ? name[..^7] : "";
                var sys = soc.Split('/')[2];
                if (!_pln.ContainsKey(name) && _pln.ContainsKey(sys + "_" + name)) name = sys + "_" + name;
                key = name;
            }
            else return null;
            if (IsSystemSocpakKey(key)) return "";
            var en = PlanetKeyName(key);
            return en == null || IsSystemName(en, system) ? "" : en;
        }

        // 惑星・月のキー (_pln のキー) の英語名 (Aggregate と同じ引き方: 星図の名前、無ければ socpak の名前を翻訳のキーとして引く)。訳が使えなければ null
        private string? PlanetKeyName(string key)
        {
            var en = _pln.TryGetValue(key, out var v) ? v : _ini.TryGetValue(key, out var iniEn) ? iniEn : null;
            return en != null && OreService.EnglishText(en) is { } usable ? usable : null;
        }

        // 星系そのものの名前か (「Stanton」「Nyx System」など。大文字小文字無視)
        private static bool IsSystemName(string name, string system) =>
            system.Length > 0 && (string.Equals(name, system, StringComparison.OrdinalIgnoreCase) || string.Equals(name, system + " System", StringComparison.OrdinalIgnoreCase));

        // 星系そのものの socpak の名前 (stantonsystem / pyrosystem / nyxsystem。Where の惑星・月のキー)
        private static bool IsSystemSocpakKey(string key) => key.Length > "system".Length && key.EndsWith("system", StringComparison.Ordinal);

        // socpak とその親を幅優先で辿り、最初に pu/system/<星系>/ の socpak が出てくる段の星系 (表示の形。その段に複数の星系があれば全部)。
        // 当たらなければ空 (pu/system の下に置かれていない socpak。テスト用・使われていないもの)
        private readonly Dictionary<string, HashSet<string>> _socSystemsCache = new(StringComparer.Ordinal);
        private HashSet<string> SocpakSystems(string soc)
        {
            if (_socSystemsCache.TryGetValue(soc, out var hit)) return hit;
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.Ordinal) { soc };
            var level = new List<string> { soc };
            for (int depth = 0; level.Count > 0 && depth < 64 && result.Count == 0; depth++)
            {
                var next = new List<string>();
                foreach (var x in level)
                {
                    var sys = SystemFromPath(x, "pu/system/");
                    if (sys.Length > 0) { result.Add(sys); continue; }
                    if (_s.Parents.TryGetValue(x, out var ps))
                        foreach (var p in ps)
                            if (seen.Add(p)) next.Add(p);
                }
                level = next;
            }
            return _socSystemsCache[soc] = result;
        }

        // パスの head の後ろのフォルダ名 (星系) を表示の形にする (stanton → Stanton。先頭だけ大文字)。head で始まらなければ ''
        private static string SystemFromPath(string path, string head)
        {
            if (!path.StartsWith(head, StringComparison.OrdinalIgnoreCase)) return "";
            var rest = path[head.Length..];
            var slash = rest.IndexOf('/');
            if (slash <= 0) return "";
            var name = rest[..slash].ToLowerInvariant();
            return char.ToUpperInvariant(name[0]) + name[1..];
        }

        // ── 出現の設定 (調査の t8_spawn.py) ──

        // SpawnDescription_ を含むレコードの SpawnDescription_Entity / SpawnDescription_NPCOption を、出てくる順に (record, 種類, 正タグ, 負タグ)
        private List<(string Record, string Kind, List<string?> Pos, List<string?> Neg)> LoadSpawnDescriptions(CancellationToken ct)
        {
            var list = new List<(string, string, List<string?>, List<string?>)>();
            _sysTags.Clear();
            foreach (var rel in _d.SpawnFiles)
            {
                ct.ThrowIfCancellationRequested();
                using var doc = JsonDocument.Parse(_d.ReadText(rel)!);
                var root = doc.RootElement;
                var rn = Str(root, "_RecordName_");
                if (root.TryGetProperty("_RecordValue_", out var rv) && rv.ValueKind == JsonValueKind.Object
                    && rv.TryGetProperty("locationData", out var ld) && ld.ValueKind == JsonValueKind.Object
                    && ld.TryGetProperty("generalTags", out var gt) && gt.ValueKind == JsonValueKind.Object
                    && gt.TryGetProperty("tags", out var g) && g.ValueKind == JsonValueKind.Array && g.GetArrayLength() > 0)
                {
                    var sys = new List<string>();
                    foreach (var x in g.EnumerateArray())
                    {
                        if (x.ValueKind != JsonValueKind.Object || !x.EnumerateObject().Any()) continue;
                        var path = _d.TagPath.TryGetValue(Str(x, "_RecordId_"), out var tp) ? tp : "";
                        if (path.Contains("Missions/LocationType/System", StringComparison.Ordinal)) sys.Add(path);
                    }
                    _sysTags[rn] = sys;
                }
                void Scan(JsonElement o)
                {
                    if (o.ValueKind == JsonValueKind.Object)
                    {
                        var t = Str(o, "_Type_");
                        if (t == "SpawnDescription_Entity")
                            list.Add((rn, "Entity", Ids(Get(o, "tags")), Ids(Get(o, "negativeTags"))));
                        if (t == "SpawnDescription_NPCOption")
                        {
                            var s = Get(o, "autoSpawnSettings");
                            var so = s is { ValueKind: JsonValueKind.Object } ? s.Value : default;
                            list.Add((rn, "NPC", Ids(so.ValueKind == JsonValueKind.Object ? Get(so, "positiveCharacterTags") : null),
                                Ids(so.ValueKind == JsonValueKind.Object ? Get(so, "negativeCharacterTags") : null)));
                        }
                        foreach (var p in o.EnumerateObject()) Scan(p.Value);
                    }
                    else if (o.ValueKind == JsonValueKind.Array)
                        foreach (var v in o.EnumerateArray()) Scan(v);
                }
                Scan(root);
            }
            return list;
        }

        private readonly Dictionary<string, List<string>> _sysTags = new(StringComparer.Ordinal);

        // 調査の str(row.get('system') or ''): 星系のタグが無ければ ""、あれば Python の並びの書き方 ['a', 'b']
        private string SystemTagsText(string record) =>
            _sysTags.TryGetValue(record, out var tags) && tags.Count > 0 ? "[" + string.Join(", ", tags.Select(t => "'" + t + "'")) + "]" : "";

        // タグの一覧 ({tags: [{_RecordId_}]}) の uuid (要素が {} でないものだけ。調査の ids)
        private static List<string?> Ids(JsonElement? tl)
        {
            var result = new List<string?>();
            if (tl is not { ValueKind: JsonValueKind.Object } t || !t.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array) return result;
            foreach (var x in tags.EnumerateArray())
                if (x.ValueKind == JsonValueKind.Object) result.Add(x.TryGetProperty("_RecordId_", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null);
            return result;
        }

        // actor が当たる出現の設定: レコードごとに最初に当たったもの (種類 Entity / NPC)。当たり = 正タグが 1 つ以上で全部当たり、負タグはどれも当たらない
        private List<(string Record, string Kind)> SpawnRowsFor(string actor, List<(string Record, string Kind, List<string?> Pos, List<string?> Neg)> spawn)
        {
            var result = new List<(string, string)>();
            if (!_d.RecordToFile.TryGetValue(actor, out var f)) return result;
            var ts = new HashSet<string>(StringComparer.Ordinal);
            using (var doc = JsonDocument.Parse(_d.ReadText(f)!))
            {
                if (doc.RootElement.TryGetProperty("_RecordValue_", out var rv) && rv.ValueKind == JsonValueKind.Object
                    && rv.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
                    foreach (var x in tags.EnumerateArray())
                        if (x.ValueKind == JsonValueKind.Object && x.TryGetProperty("_RecordId_", out var id) && id.ValueKind == JsonValueKind.String)
                            AddWithAncestors(ts, id.GetString()!);
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (rec, kind, pos, neg) in spawn)
            {
                if (pos.Count == 0 || !pos.All(p => p != null && ts.Contains(p)) || neg.Any(n => n != null && ts.Contains(n))) continue;
                if (seen.Add(rec)) result.Add((rec, kind));
            }
            return result;
        }

        private void AddWithAncestors(HashSet<string> ts, string id)
        {
            string? u = id;
            var guard = 0;
            while (!string.IsNullOrEmpty(u) && guard++ < 1000)
            {
                ts.Add(u);
                u = _d.TagParent.TryGetValue(u, out var p) ? p : null;
            }
        }

        // ── ルート表の評価 (調査の t2b_eval.py。H1〜H5 は調査で決めたタグの読み方。推定) ──

        // 品 → それが選ばれうる LootConfig の (持ち主の record 名, 表のファイル名)。プールの絞り込みと二次選択の両方を通ったものだけ
        private Dictionary<string, List<(string Owner, string Table)>> EvaluateLoot(List<string> entities, CancellationToken ct)
        {
            var result = new Dictionary<string, List<(string, string)>>(StringComparer.Ordinal);
            var lower = new Dictionary<string, (string Record, HashSet<string> Tags)>(StringComparer.Ordinal);
            foreach (var (rn, tags) in _d.EntityTags) lower[Short(rn).ToLowerInvariant()] = (rn, tags);
            var targets = new List<(string It, string Name, HashSet<string> Ts)>();
            foreach (var it in entities)
            {
                if (!lower.TryGetValue(it.ToLowerInvariant(), out var e)) continue;
                var ts = new HashSet<string>(StringComparer.Ordinal);
                foreach (var u in e.Tags) AddWithAncestors(ts, u);
                targets.Add((it, e.Record, ts));
            }
            if (targets.Count == 0) return result;
            foreach (var rel in _d.LootConfigFiles)
            {
                ct.ThrowIfCancellationRequested();
                using var doc = JsonDocument.Parse(_d.ReadText(rel)!);
                var owner = Str(doc.RootElement, "_RecordName_");
                var acc = new List<JsonElement>();
                FindLootConfigs(doc.RootElement, acc);
                foreach (var lc in acc)
                {
                    var c = Get(lc, "lootConstraints") is { } cc && Truthy(cc) ? cc : (JsonElement?)null;
                    var v3s = Get(lc, "lootTableV3");
                    var v2s = Get(lc, "lootTable");
                    var v3 = v3s is { } a3 && Truthy(a3) ? Ref(a3) : null;
                    var v2 = v2s is { } a2 && Truthy(a2) ? Ref(a2) : null;
                    var tableStr = v3s is { } t3 && Truthy(t3) ? StrOf(t3) : v2s is { } t2 && Truthy(t2) ? StrOf(t2) : "";
                    var table = tableStr[(tableStr.LastIndexOf('/') + 1)..];
                    foreach (var (it, name, ts) in targets)
                    {
                        var hv3 = v3 != null && TableV3(v3.Value, name, ts);
                        var hv2 = v2 != null && TableV2(v2.Value, ts);
                        if (!hv3 && !hv2) continue;
                        var pf = c is { } c1 && Get(c1, "poolFilter") is { } pfs && Truthy(pfs) ? Pool(Ref(pfs), ts) : true;
                        var sc = c is { } c2 ? Secondary(Get(c2, "secondaryChoices"), ts) : true;
                        if (!(pf && sc)) continue;
                        if (!result.TryGetValue(it, out var list)) result[it] = list = new List<(string, string)>();
                        list.Add((owner, table));
                    }
                }
            }
            return result;
        }

        private static void FindLootConfigs(JsonElement o, List<JsonElement> acc)
        {
            if (o.ValueKind == JsonValueKind.Object)
            {
                if (Str(o, "_Type_") == "LootConfig") acc.Add(o);
                foreach (var p in o.EnumerateObject()) FindLootConfigs(p.Value, acc);
            }
            else if (o.ValueKind == JsonValueKind.Array)
                foreach (var v in o.EnumerateArray()) FindLootConfigs(v, acc);
        }

        private static readonly Regex RefTailRegex = new("/([^/]+)\\.json$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // 参照の文字 (file://…/x.json) のレコードを読む (ファイル名で引く。調査の ref)。文字でない・空・引けなければ null
        private JsonElement? Ref(JsonElement? v)
        {
            if (v is not { ValueKind: JsonValueKind.String } s) return null;
            var str = s.GetString() ?? "";
            if (str.Length == 0) return null;
            var m = RefTailRegex.Match(str);
            if (!m.Success) return null;
            var b = m.Groups[1].Value.ToLowerInvariant();
            // records の下のパスで引き、引けなければファイル名で引く (RefText と同じ。同じ名前の別のファイルを取り違えないように)。覚えるのは引いた rel で
            var tRel = DeliverableDcb.RecordsRelOfRef(str) is { } pl && _d.RelByLower.TryGetValue(pl, out var byPath) ? byPath
                : _d.BaseToRel.TryGetValue(b, out var byBase) ? byBase : null;
            if (tRel == null) return null;
            if (_refCache.TryGetValue(tRel, out var hit)) return hit;
            JsonElement? result = null;
            if (_d.ReadText(tRel) is { } bText)
            {
                var doc = JsonDocument.Parse(bText);
                _docs.Add(doc);
                result = doc.RootElement;
            }
            return _refCache[tRel] = result;
        }

        private static string? Tid(JsonElement? t) =>
            t is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("_RecordId_", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;

        // TagsDNFTerm: 正タグはすべて当たり、負タグはどれも当たらない (H2)。項が無い・空なら当たり
        private static bool Dnf(JsonElement? term, HashSet<string> ts)
        {
            if (term is not { } t || !Truthy(t)) return true;
            if (t.ValueKind != JsonValueKind.Object) return true;
            var pos = Arr(Get(t, "positiveTags")).Select(x => Tid(x)).ToList();
            var neg = Arr(Get(t, "negativeTags")).Select(x => Tid(x)).ToList();
            return pos.All(p => p != null && ts.Contains(p)) && !neg.Any(n => n != null && ts.Contains(n));
        }

        private bool Pool(JsonElement? pf, HashSet<string> ts)
        {
            if (pf is not { } p || p.ValueKind != JsonValueKind.Object) return true;
            var f = p.TryGetProperty("_RecordValue_", out var rv) ? Get(rv, "filter") : p;
            return PoolF(f, ts);
        }

        // PoolFilter: Cumulative はすべて通り、または Additive のどれかに当たる (H3)
        private bool PoolF(JsonElement? f, HashSet<string> ts)
        {
            if (f is not { ValueKind: JsonValueKind.Object } o) return true;
            var t = Str(o, "_Type_");
            if (t == "PoolFilter_Tags") return Dnf(Get(o, "tagFilter"), ts);
            if (t == "PoolFilter_RecordRef") return Pool(Ref(Get(o, "filterRecord")), ts);
            if (t == "PoolFilter_Sequence")
            {
                bool cum = true, add = false;
                foreach (var inst in Arr(Get(o, "filters")))
                {
                    var r = PoolF(Get(inst, "filter"), ts);
                    if (Str(inst, "mode") == "Cumulative") cum = cum && r;
                    else add = add || r;
                }
                return cum || add;
            }
            return true;
        }

        // 二次選択: 各層で、重み 0 より大の選択肢のうち品に当たるものが 1 つ以上 (H4)
        private bool Secondary(JsonElement? sc, HashSet<string> ts)
        {
            if (sc is not { } s || !Truthy(s) || s.ValueKind != JsonValueKind.Object) return true;
            var t = Str(s, "_Type_");
            var layers = new List<JsonElement?>();
            if (t == "LootV3SecondaryChoicesRecordRef_SingleLayer") layers.Add(Ref(Get(s, "singleLayerRecord")));
            else
            {
                var ml = Ref(Get(s, "multiLayerRecord"));
                if (ml is { } m && m.TryGetProperty("_RecordValue_", out var mv))
                    foreach (var x in Arr(Get(mv, "layers"))) layers.Add(Ref(x));
            }
            foreach (var l in layers)
            {
                if (l is not { } L || !Truthy(L) || !L.TryGetProperty("_RecordValue_", out var lv)) continue;
                var ok = Arr(Get(lv, "choices")).Any(c => Num(Get(c, "weight")) > 0 && Dnf(Get(c, "selector") is { ValueKind: JsonValueKind.Object } sel ? Get(sel, "tags") : null, ts));
                if (!ok) return false;
            }
            return true;
        }

        private bool ArchV3(JsonElement? a, string name, HashSet<string> ts)
        {
            if (a is not { ValueKind: JsonValueKind.Object } o) return false;
            if (Str(o, "_Type_") == "LootArchetypeV3_RecordRef")
            {
                var r = Ref(Get(o, "lootArchetypeRecord"));
                return ArchV3(r is { } rr && rr.TryGetProperty("_RecordValue_", out var rv) ? Get(rv, "lootArchetype") : null, name, ts);
            }
            var shortLower = "/" + Short(name).ToLowerInvariant() + ".json";
            foreach (var e in Arr(Get(o, "entries")))
            {
                if (Get(e, "selector") is not { ValueKind: JsonValueKind.Object } s) continue;
                var st = Str(s, "_Type_");
                if (st == "LootArchetypeV3Selector_Tags" && Dnf(Get(s, "tags"), ts) && Num(Get(e, "weight")) > 0) return true;
                if (st == "LootArchetypeV3Selector_EntityClasses" && Get(s, "entityClasses") is { ValueKind: JsonValueKind.Object } ec)
                    foreach (var p in Arr(Get(ec, "entityClasses")))
                        if (p.ValueKind == JsonValueKind.String && (p.GetString() ?? "").ToLowerInvariant().EndsWith(shortLower, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private bool TableV3(JsonElement tb, string name, HashSet<string> ts)
        {
            if (!tb.TryGetProperty("_RecordValue_", out var rv) || Get(rv, "lootTable") is not { ValueKind: JsonValueKind.Object } lt) return false;
            foreach (var e in Arr(Get(lt, "lootArchetypes")))
                if (Num(Get(e, "weight")) > 0 && ArchV3(Get(e, "archetype"), name, ts)) return true;
            return false;
        }

        // V2 の LootArchetype: 除外タグに当たらず、主の群のどれか (重み > 0) に当たり、二次の群 (Rarity など) の各群で重み > 0 の項目に当たる (H5)
        private static bool ArchV2(JsonElement a, HashSet<string> ts)
        {
            if (!a.TryGetProperty("_RecordValue_", out var vv) || vv.ValueKind != JsonValueKind.Object) return false;
            var ex = Get(vv, "excludedTags");
            if (ex is { } exo && Truthy(exo) && exo.ValueKind == JsonValueKind.Object && Arr(Get(exo, "tags")).Any(x => Tid(x) is { } id && ts.Contains(id))) return false;
            var g = Get(vv, "primaryOrGroup");
            var entries = g is { ValueKind: JsonValueKind.Object } go ? Arr(Get(go, "entries")) : [];
            var prim = entries.Any(e => Tid(Get(e, "tag")) is { } id && ts.Contains(id) && Dnf(Get(e, "additionalTags"), ts) && WeightOr1(e) > 0);
            if (!prim) return false;
            foreach (var sg in Arr(Get(vv, "secondaryOrGroups")))
                if (!Arr(Get(sg, "entries")).Any(e => Tid(Get(e, "tag")) is { } id && ts.Contains(id) && WeightOr1(e) > 0)) return false;
            return true;
        }

        private static double WeightOr1(JsonElement e) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("weight", out var w) ? Num(w) : 1;

        private bool TableV2(JsonElement tb, HashSet<string> ts)
        {
            if (!tb.TryGetProperty("_RecordValue_", out var rv)) return false;
            bool hit = false;
            void Wa(JsonElement o)
            {
                if (hit) return;
                if (o.ValueKind == JsonValueKind.Object)
                {
                    if (Str(o, "_Type_") == "WeightedLootArchetype" && Ref(Get(o, "archetype")) is { } a && Truthy(a) && ArchV2(a, ts)) { hit = true; return; }
                    foreach (var p in o.EnumerateObject()) Wa(p.Value);
                }
                else if (o.ValueKind == JsonValueKind.Array)
                    foreach (var v in o.EnumerateArray()) Wa(v);
            }
            Wa(rv);
            return hit;
        }

        // ── JSON の小道具 (Python の dict.get と真偽の扱いに合わせる) ──
        private static JsonElement? Get(JsonElement o, string key) =>
            o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v) ? v : null;
        private static JsonElement? Get(JsonElement? o, string key) => o is { } e ? Get(e, key) : null;
        private static string Str(JsonElement o, string key) =>
            o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        private static string StrOf(JsonElement v) => v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        private static List<JsonElement> Arr(JsonElement? v) => v is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray().ToList() : [];
        private static double Num(JsonElement? v) => v is { ValueKind: JsonValueKind.Number } n ? n.GetDouble() : v is { ValueKind: JsonValueKind.True } ? 1 : 0;
        private static bool Truthy(JsonElement v) => v.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => false,
            JsonValueKind.String => (v.GetString() ?? "").Length > 0,
            JsonValueKind.Array => v.GetArrayLength() > 0,
            JsonValueKind.Object => v.EnumerateObject().Any(),
            JsonValueKind.Number => v.GetDouble() != 0,
            _ => true,
        };
    }
}
