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
    // 各段の時間を StatusChanged に出す (構築の時間がどれだけ増えたかを実測できるように)
    private async Task<int> ExtractDeliverableSourcesAsync(string p4kPath, CancellationToken ct)
    {
        const string P = "[納品物の入手場所]";
        var total = Stopwatch.StartNew();
        var (entities, resources) = ReadDeliverableTargets();
        if (entities.Count + resources.Count == 0)
        {
            StatusChanged?.Invoke($"{P} Wikelo の契約の納品物がありません (前のデータを残します)");
            return 0;
        }
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

            // 3. ロードアウトの XML (Data/Scripts/Loadouts/**) の itemName
            if (!HasDeliverableSpace(dir, "ロードアウトの XML")) return 0;
            sw.Restart();
            StatusChanged?.Invoke($"{P} ロードアウトの XML を読み込み中...");
            var loDir = Path.Combine(dir, "loadouts");
            Directory.CreateDirectory(loDir);
            await RunStarBreakerAsync($"p4k extract --p4k \"{p4kPath}\" -o \"{loDir}\" --filter \"Data/Scripts/Loadouts/**\"", ct);
            var (loadoutItems, loadoutFiles, loadoutBad) = await Task.Run(() => DeliverableDcb.ReadLoadoutItemNames(Path.Combine(loDir, "Data"), ct), ct);
            var tLoadout = sw.Elapsed.TotalSeconds;

            // 4. socpak
            sw.Restart();
            StatusChanged?.Invoke($"{P} Data.p4k の socpak を読み込み中...");
            var soc = await Task.Run(() => DeliverableSocpaks.Scan(p4kPath, dcb, ct), ct);
            var tSoc = sw.Elapsed.TotalSeconds;

            // 5. 照合
            sw.Restart();
            var loc = LoadShipNameLocalization();
            var rows = await Task.Run(() => new DeliverableSourceFinder(dcb, soc, loadoutItems, loc).Find(entities, resources, ct), ct);
            var tFind = sw.Elapsed.TotalSeconds;

            // 6. 書き込み (入れ替え)
            ct.ThrowIfCancellationRequested();
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

        private static readonly Regex FileRefRegex = new("file://[^\"]*/([^/\"]+)\\.json\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);
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
                foreach (Match fm in FileRefRegex.Matches(text))
                {
                    var b = fm.Groups[1].Value.ToLowerInvariant();
                    if (b != baseName) refs.Add(b);
                }
                res.RefBases = refs.ToArray();
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

        // 品 (資源は MineableElement) を参照するレコードを上へ辿る (調査の upchain。6 段まで、契約・UI などの参照は辿らない)
        private List<(string? Rec, List<string> Path)> UpChain(string baseName, int maxd = 6)
        {
            var result = new List<(string?, List<string>)>();
            var seen = new HashSet<string>(StringComparer.Ordinal) { baseName };
            var fr = new Queue<(string X, int Dp, List<string> Path)>();
            fr.Enqueue((baseName, 0, new List<string> { baseName }));
            while (fr.Count > 0)
            {
                var (x, dp, path) = fr.Dequeue();
                if (!_d.Rev.TryGetValue(x, out var rs)) continue;
                foreach (var r in rs)
                {
                    if (Skip.Any(p => r.StartsWith(p, StringComparison.Ordinal))) continue;
                    var b = DeliverableDcb.BaseOf(r);
                    if (seen.Contains(b) || dp >= maxd) continue;
                    seen.Add(b);
                    var np = new List<string>(path) { b };
                    result.Add((_d.Rec.TryGetValue(r, out var rn) ? rn : null, np));
                    fr.Enqueue((b, dp + 1, np));
                }
            }
            return result;
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
                    if (!_d.Rev.TryGetValue(DeliverableDcb.BaseOf(f), out var rs)) continue;
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
            if (_d.BaseToRecord.TryGetValue(k, out var rn) && _s.RecordToSocpaks.TryGetValue(rn, out var own))
                foreach (var s in own) src.Add(new Src("配置", Short(rn), s, DeliverableSourceSections.Placed));
            // A DCB を上へ
            var chain = UpChain(k);
            foreach (var (r, path) in chain)
            {
                if (r == null || r.StartsWith("StarMapObject", StringComparison.Ordinal) || r.StartsWith("BuildingBlocks", StringComparison.Ordinal)) continue;
                if (!_s.RecordToSocpaks.TryGetValue(r, out var socs)) continue;
                var section = ChainSection(r, path);
                foreach (var s in socs) src.Add(new Src("参照:" + TypeOf(r), string.Join(" > ", path.Skip(1)), s, section));
            }
            // B 生き物 (ロードアウト)
            var lkeys = new List<string> { k };
            foreach (var (r, path) in chain)
                if (r != null && r.StartsWith("EntityClassDefinition", StringComparison.Ordinal)) lkeys.Add(path[^1]);
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
                        && _d.Rev.TryGetValue(DeliverableDcb.BaseOf(f), out var rs))
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
            foreach (var b in path.Skip(1))
                if (_d.BaseToRecord.TryGetValue(b, out var prn)) types.Add(TypeOf(prn));
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
                foreach (var (r, path) in UpChain(DeliverableDcb.BaseOf(rel)))
                {
                    if (r == null || r.StartsWith("StarMapObject", StringComparison.Ordinal) || r.StartsWith("BuildingBlocks", StringComparison.Ordinal)) continue;
                    if (!_s.RecordToSocpaks.TryGetValue(r, out var socs)) continue;
                    foreach (var s in socs)
                        src.Add(new Src("参照:" + TypeOf(r), Short(rn) + " > " + string.Join(" > ", path.Skip(1)), s, DeliverableSourceSections.Mining));
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
            if (_refCache.TryGetValue(b, out var hit)) return hit;
            JsonElement? result = null;
            if (_d.BaseToRel.TryGetValue(b, out var bRel) && _d.ReadText(bRel) is { } bText)
            {
                var doc = JsonDocument.Parse(bText);
                _docs.Add(doc);
                result = doc.RootElement;
            }
            return _refCache[b] = result;
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
