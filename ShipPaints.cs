using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

// 船の塗装 (ペイント) の一覧・保存した塗装・3D の画面で塗装を切り替えるための材質の表
// (2026-11-05、利用者の指示「因みに船の 3D はオプション塗装を適応させることができる？ 可能であれば右端に縦のリストでバリエーションを表示して切替で見れるようにしたい。
//  船舶の持っているリストのオプションに塗装も出して、所持船のリストでも自分の持っている塗装をリスト選択で自動保存して ３D の表示時にデフォルトとして適応できるようにしてほしい」・
//  「"船・装備・武器の 3D は常に色付け" これは設定で切れるようにしてください。」)。
//
// 塗装の一覧 (ListFor):
//   Data.p4k の DataCore から、塗装の品 (EntityClassDefinition のうち AttachDef.Type が Paints のもの) と、船の塗装の口 (hardpoint_paint) の
//   portTags・requiredTags、船の SubGeometry の Tags を読んで、船ごとに付けられる塗装を決める (BuildList。ゲームデータの読み直しの構築とは別の軽い処理)。
//   結果は 3D の置き場所 (Ship3DExports.Folder) の ship3d_paints\paints.json に置き、Data.p4k の印 (パス・大きさ・更新日時。Ship3DExports.P4kStamp) か
//   この処理の版 (ListVersion) が変わったら作り直す (要るとき = 初めて一覧を引いたときに 1 回)。
//   付けられる塗装の判定はデータに書かれておらず、推定 (調査担当の判定 r1 と同じ):
//     口の requiredTags (空白区切り) が全部、塗装の品の AttachDef.Tags にあり、かつ 塗装の品の RequiredTags が全部、口の portTags にある
//     (タグは大文字小文字を区別して比べる) こと。そのうえで、塗装の品の Tags の最初の「@」の語 (= 船の SubGeometry の Tags) が、
//     その船の SubGeometry の Tags にある (大文字小文字を区別しない。ShipExport.ApplyPaint が当てられる) ものだけを出す。
//   口は DataCore の SItemPortContainerComponentParams.Ports[] の hardpoint_paint があればそれ、無ければ船の XML (vehicleDefinition) の
//   <Part name="hardpoint_paint" class="ItemPort"> の最初の <ItemPort>。改造 (VehicleComponentParams.modification) があれば、その <Modification> の
//   patchFile の同じ id の <ItemPort> で置き換え、<Elem idRef="その id"> の値を足す。
//   塗装が SubGeometry ではなく Material.materialVariants にある型の船 (300 系・Hermes・Ballista など) は、ShipExport.ApplyPaint が当てられないので出さない。
//   名前: 英語はゲームの英語の global.ini (Data.p4k の中) の AttachDef.Localization.Name のキー、日本語は translations.db の同じキー (引いたときに読む)。
//
// 保存した塗装 (SavedFor): trade_cache.db の ship_loadouts の口 PortName (hardpoint_paint) の行 (item_record = 塗装の品の record)。
//   画面から書くのは［装備］［所持船］の画面 (第 2 段)。3D の画面で切り替えただけでは書かない。
//
// 材質の表 (TablePath・BuildTable): 塗装ごと (と、塗装なし・色付けなし) に、書き出しの処理を塗装つきで走らせ (ShipExport.ExportJson。GLB は書かない)、
//   GLB の JSON の materials と、mesh ごと・primitive ごとの材質の番号だけを取り出したもの。形・画像は今の GLB のまま使い、ビューアが材質だけを差し替える。
//   今の GLB と形の部分 (asset・materials・primitive の material・node の extras・extensionsUsed を除いた JSON) が同じことを確かめてから作る (違えば作らない)。
//   置き場所: 取り込み済みの GLB (置き場所の <短い名前>.glb) なら ship3d_paints\<短い名前>\<塗装>.json、保存しない設定で直接読んだ GLB (作業用のフォルダ) なら
//   その GLB の横 (<GLB の名前>_<塗装の MD5>.json。表示をやめたら GLB と一緒に消す。残ったものは次の起動の掃除で消える)。
//   鮮度: 表に作ったときの GLB の大きさ・更新日時・Data.p4k の印・表の版 (TableVersion) を入れ、どれかが今と違えば作り直す (GLB を取り込み直したら、その船の表も作り直しになる)
internal static class ShipPaints
{
    // ship_loadouts.port_name に入れる塗装の口の名前 (ゲームデータの口の名前と同じ。［装備］［所持船］の画面 (第 2 段) もこれを使う)
    public const string PortName = "hardpoint_paint";
    // 3D の画面の一覧の特別な塗装: 標準 (今の GLB そのまま = 引数なしの書き出しの姿。表は作らない)、塗装なし (既定のロードアウトに塗装の入った船で、塗装を当てない姿)、
    // 色付けなし (設定で色付けを切ったとき。パレットの色を当てない姿)
    public const string IdStandard = "", IdNone = "~none", IdNoPalette = "~nopal";
    // 標準の材質の表 (引数なしの書き出しの材質を今のコードで作ったもの)。取り込み済みの GLB が古い版の材質の決め方で書かれているとき (IsOldGlb)、
    // 「標準」の姿にこれを当てる (2026-11-05、親の決定。取り込み直さなくても標準の姿が今の決まりになり、塗装・色付けなしと混ざらないように)
    public const string IdStandardTable = "~std";

    // 付けられる塗装 1 つ: 品の短い名前 (例 Paint_Gladius_Valiant)・英語名 (ゲーム中と同じ)・日本語名 (無ければ null)
    public sealed record Paint(string Record, string English, string? Japanese);

    // 一覧の保存の版 (作り方を変えたら上げる。違えば作り直す)
    private const int ListVersion = 1;
    // 材質の表の版
    private const int TableVersion = 1;
    private const string AppMark = "StarCitizenJapaneseTextCreater.ship3d.paints";

    public static string Folder => Path.Combine(Ship3DExports.Folder, "ship3d_paints");
    private static string ListPath => Path.Combine(Folder, "paints.json");

    private static void Log(string msg)
    {
        try { App.AppendStartupLog(App.Config.WorkingDirectory, $"[3D モデル] {msg}"); } catch { }
    }

    // ===== 塗装の一覧 =====

    // 一覧の保存の中身
    internal sealed class ListData
    {
        public string App { get; set; } = "";
        public int Version { get; set; }
        public string P4kPath { get; set; } = "";
        public long P4kSize { get; set; }
        public long P4kWriteUtcTicks { get; set; }
        public string BuiltUtc { get; set; } = "";
        public double Seconds { get; set; }
        // 塗装の品の短い名前 → 名前のキー (「@」を除いたもの) と英語名
        public Dictionary<string, PaintInfo> Paints { get; set; } = new();
        // 船の短い名前 → 付けられる塗装 (品の短い名前。並びは英語名の順) と、既定のロードアウトに入っていて当たる塗装 (無ければ null)
        public Dictionary<string, ShipInfo> Ships { get; set; } = new();
    }
    internal sealed class PaintInfo
    {
        public string LocKey { get; set; } = "";
        public string English { get; set; } = "";
    }
    internal sealed class ShipInfo
    {
        public List<string> Paints { get; set; } = new();
        public string? DefaultPaint { get; set; }
    }

    private static readonly object ListLock = new();
    private static ListData? _list;
    private static Dictionary<string, ShipInfo>? _ships;   // _list.Ships を大文字小文字を区別しない形にしたもの

    // その船に付けられる塗装 (英語名の順)。無ければ空。一覧がまだ無い・古ければ作る (数秒かかるので UI スレッドでは ListForAsync を使う)。
    // ship: record (EntityClassDefinition.<短い名前>) か短い名前。ゲームが見つからない・読めなければ空 (ログに残す)
    public static IReadOnlyList<Paint> ListFor(string ship)
    {
        if (Ensure() is not { } snap || !snap.Ships.TryGetValue(Ship3DExports.ShortName(ship), out var info) || info.Paints.Count == 0) return [];
        var data = snap.Data;
        var keys = info.Paints.Select(p => data.Paints.TryGetValue(p, out var pi) ? pi.LocKey : "").ToList();
        var ja = JapaneseNames(keys, Path.Combine(DetailPanel.WorkDir(), "translations.db"));
        return info.Paints.Select((p, i) => new Paint(p, data.Paints.TryGetValue(p, out var pi) && pi.English.Length > 0 ? pi.English : p, ja.GetValueOrDefault(keys[i]))).ToList();
    }

    public static Task<IReadOnlyList<Paint>> ListForAsync(string ship) => Task.Run(() => ListFor(ship));

    // ListFor と同じ。ただし一覧を用意できなかった (ゲームが見つからない・読めない) ときは false (「付けられる塗装が無い」と分けるため。HangarService.PruneLoadouts)
    public static bool TryListFor(string ship, out IReadOnlyList<Paint> list)
    {
        list = [];
        if (Ensure() == null) return false;
        list = ListFor(ship);
        return true;
    }

    // 画面に出す塗装の名前: 英語名 (ゲーム中と同じ) を主に、日本語名があれば添える「英語名 (日本語名)」
    public static string DisplayName(Paint p) =>
        !string.IsNullOrEmpty(p.Japanese) && !p.Japanese.Equals(p.English, StringComparison.OrdinalIgnoreCase) ? $"{p.English} ({p.Japanese})" : p.English;

    // ===== 持っている塗装 (My Hangar の同期の hangar_items の kind 'Skin') =====
    // 同期の名前とゲームの英語名の照らし方 (調査担当が「44 件が 1 対 1 で合う」とした照らし方): 小文字にし、英字・数字以外を空白にして、
    // 「paint」「livery」の語を除き、残りの語を空白 1 つでつないだものが同じなら同じ塗装。合わないものは結ばない (推測で結ばない)
    internal static string SkinKey(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.ToLowerInvariant()) sb.Append(char.IsAsciiLetterOrDigit(ch) ? ch : ' ');
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w is not "paint" and not "livery"));
    }

    // 持っている塗装 (品の短い名前の集まり)。同期の塗装の名前が、ゲームの塗装の英語名と SkinKey で合うもの。
    // 一覧を用意できない・読めなければ空。Titles: 同期の塗装の数 (重複を除いた名前の数)、Matched: そのうちゲームの塗装と合った数
    internal static (HashSet<string> Records, int Titles, int Matched) Owned(string workDir)
    {
        var res = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var titles = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var db = Path.Combine(workDir, "trade_cache.db");
            if (!File.Exists(db)) return (res, 0, 0);
            using var c = new TrackedSqliteConnection($"Data Source={db};Mode=ReadOnly");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT title FROM hangar_items WHERE kind = 'Skin'";
            using var r = cmd.ExecuteReader();
            while (r.Read()) if (!r.IsDBNull(0) && SkinKey(r.GetString(0)) is { Length: > 0 } k) titles.Add(k);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException) { return (res, 0, 0); }
        if (titles.Count == 0 || Ensure() is not { } snap) return (res, titles.Count, 0);
        var byKey = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (rec, info) in snap.Data.Paints)
        {
            if (info.English.Length == 0) continue;
            var k = SkinKey(info.English);
            if (k.Length == 0) continue;
            if (!byKey.TryGetValue(k, out var l)) byKey[k] = l = new();
            l.Add(rec);
        }
        int matched = 0;
        foreach (var t in titles)
        {
            if (!byKey.TryGetValue(t, out var l)) continue;
            matched++;
            foreach (var rec in l) res.Add(rec);
        }
        return (res, titles.Count, matched);
    }

    // 保存した塗装の行 (ship_key → 塗装の品の短い名前)。読めなければ空
    internal static Dictionary<string, string> SavedByKey(string workDir)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var db = Path.Combine(workDir, "trade_cache.db");
            if (!File.Exists(db)) return d;
            using var c = new TrackedSqliteConnection($"Data Source={db};Mode=ReadOnly");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT ship_key, item_record FROM ship_loadouts WHERE port_name = @p";
            cmd.Parameters.AddWithValue("@p", PortName);
            using var r = cmd.ExecuteReader();
            while (r.Read()) if (!r.IsDBNull(0) && !r.IsDBNull(1) && r.GetString(1).Length > 0) d[r.GetString(0)] = Ship3DExports.ShortName(r.GetString(1));
        }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException) { }
        return d;
    }


    // 既定のロードアウトに入っていて当たる塗装 (品の短い名前。無ければ null)。この船の 3D の「標準」はこの塗装の姿
    public static string? DefaultPaintOf(string ship) =>
        Ensure() is { } snap && snap.Ships.TryGetValue(Ship3DExports.ShortName(ship), out var s) ? s.DefaultPaint : null;

    private sealed record Snapshot(ListData Data, Dictionary<string, ShipInfo> Ships);

    // 一覧を用意する (保存したものが今の Data.p4k のものなら読む、無ければ作って保存する)。用意できなければ null
    private static Snapshot? Ensure()
    {
        var stamp = Ship3DExports.CurrentStamp();
        if (stamp == null) return null;
        lock (ListLock)
        {
            if (_list != null && Matches(_list, stamp)) return new Snapshot(_list, _ships!);
            _list = null; _ships = null;
            try
            {
                if (File.Exists(ListPath) && JsonSerializer.Deserialize<ListData>(File.ReadAllText(ListPath)) is { } saved && Matches(saved, stamp))
                {
                    Set(saved);
                    return new Snapshot(_list!, _ships!);
                }
            }
            catch (Exception ex) { Log($"塗装の一覧 ({ListPath}) を読めませんでした (作り直します): {ex.Message}"); }
            try
            {
                var sw = Stopwatch.StartNew();
                var data = BuildList(P4kArchive.Shared(stamp.Path), DataCoreShared.Get(null, stamp.Path));
                data.P4kPath = stamp.Path; data.P4kSize = stamp.Size; data.P4kWriteUtcTicks = stamp.WriteUtcTicks;
                data.Seconds = Math.Round(sw.Elapsed.TotalSeconds, 2);
                Set(data);
                try
                {
                    Directory.CreateDirectory(Folder);
                    var tmp = ListPath + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(data), Encoding.UTF8);
                    File.Move(tmp, ListPath, overwrite: true);
                }
                catch (Exception ex) { Log($"塗装の一覧を保存できませんでした (今回の起動の間だけ使います): {ex.Message}"); }
                Log($"塗装の一覧を作りました: 塗装 {data.Paints.Count} 件、塗装のある船 {data.Ships.Count} 隻、{data.Seconds:F1} 秒 (DataCore の読み込みを含む)");
                return new Snapshot(_list!, _ships!);
            }
            catch (Exception ex)
            {
                Log($"塗装の一覧を作れませんでした: {ex.GetType().FullName}: {ex.Message}");
                return null;
            }
        }
    }

    private static bool Matches(ListData d, Ship3DExports.P4kStamp stamp) =>
        d.App == AppMark && d.Version == ListVersion && string.Equals(d.P4kPath, stamp.Path, StringComparison.OrdinalIgnoreCase) && d.P4kSize == stamp.Size && d.P4kWriteUtcTicks == stamp.WriteUtcTicks;

    private static void Set(ListData d)
    {
        _list = d;
        _ships = new Dictionary<string, ShipInfo>(d.Ships, StringComparer.OrdinalIgnoreCase);
    }

    // 塗装の品 1 つ (読み取りの途中)
    private sealed record PaintDef(string Name, HashSet<string> Tags, string[] Req, string? At, string LocKey);
    // 塗装の口
    private sealed record PortDef(string[] PortTags, string[] RequiredTags);

    private static readonly HashSet<string> SubGeomFields = new(StringComparer.Ordinal) { "Geometry", "SubGeometry", "Tags" };
    private static readonly HashSet<string> PortFields = new(StringComparer.Ordinal) { "Ports", "Name", "PortTags", "RequiredPortTags" };
    private static readonly HashSet<string> LoadoutFields = new(StringComparer.Ordinal) { "loadout", "entries", "itemPortName", "entityClassName", "entityClassReference" };

    // 一覧を作る (Data.p4k の印は入れない。呼ぶ側で入れる)
    internal static ListData BuildList(P4kArchive p4k, DataCoreDb db)
    {
        var data = new ListData { App = AppMark, Version = ListVersion, BuiltUtc = DateTime.UtcNow.ToString("o") };
        if (db.StructIndexByName("EntityClassDefinition") is not int ecd) return data;
        var cType = ShipExport.TryCompile(db, ecd, "Components[SAttachableComponentParams].AttachDef.Type", DataCoreDb.StringLeaf);
        var cTags = ShipExport.TryCompile(db, ecd, "Components[SAttachableComponentParams].AttachDef.Tags", DataCoreDb.StringLeaf);
        var cReq = ShipExport.TryCompile(db, ecd, "Components[SAttachableComponentParams].AttachDef.RequiredTags", DataCoreDb.StringLeaf);
        var cLoc = ShipExport.TryCompile(db, ecd, "Components[SAttachableComponentParams].AttachDef.Localization.Name", DataCoreDb.StringLeaf);
        var cGeom = ShipExport.TryCompile(db, ecd, "Components[SGeometryResourceParams]", DataCoreDb.AnyLeaf);
        var cPorts = ShipExport.TryCompile(db, ecd, "Components[SItemPortContainerComponentParams]", DataCoreDb.AnyLeaf);
        var cLoadout = ShipExport.TryCompile(db, ecd, "Components[SEntityComponentDefaultLoadoutParams]", DataCoreDb.AnyLeaf);
        var cVd = ShipExport.TryCompile(db, ecd, "Components[VehicleComponentParams].vehicleDefinition", DataCoreDb.StringLeaf);
        var cMod = ShipExport.TryCompile(db, ecd, "Components[VehicleComponentParams].modification", DataCoreDb.StringLeaf);
        if (cType == null || cTags == null || cGeom == null) return data;

        static string ShortOf(DataCoreDb db, DataCoreDb.Record r) { var n = db.String2(r.NameOffset); return n[(n.LastIndexOf('.') + 1)..]; }
        static string[] Words(string? s) => (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        // 1. 塗装の品
        var paints = new List<PaintDef>();
        var ecdRecords = new List<DataCoreDb.Record>();
        foreach (var r in db.Records)
        {
            if (r.StructIndex != ecd) continue;
            ecdRecords.Add(r);
            if (ShipExport.TryString(db, cType, r) != "Paints") continue;
            var tags = Words(ShipExport.TryString(db, cTags, r));
            var req = cReq == null ? [] : Words(ShipExport.TryString(db, cReq, r));
            var loc = (cLoc == null ? null : ShipExport.TryString(db, cLoc, r)) ?? "";
            var at = tags.FirstOrDefault(t => t.StartsWith('@'))?[1..];
            paints.Add(new PaintDef(ShortOf(db, r), new HashSet<string>(tags, StringComparer.Ordinal), req, at, loc.StartsWith('@') ? loc[1..] : loc));
        }
        var atTags = new HashSet<string>(paints.Where(p => p.At != null).Select(p => p.At!), StringComparer.OrdinalIgnoreCase);

        // 2. 英語名 (ゲームの英語の global.ini)
        var en = EnglishIni(p4k);
        foreach (var p in paints)
        {
            var name = p.LocKey.Length == 0 ? null : en.TryGetValue(p.LocKey, out var v) ? v : en.TryGetValue(p.LocKey + ",P", out var v2) ? v2 : null;
            data.Paints[p.Name] = new PaintInfo { LocKey = p.LocKey, English = (name ?? "").Trim() };
        }

        // 3. 船: SubGeometry の Tags に塗装の「@」の語があるもの
        var xmlCache = new Dictionary<string, XElement?>(StringComparer.OrdinalIgnoreCase);
        var paintByName = paints.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var r in ecdRecords)
        {
            var subTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var comp in ShipExport.TryValues(db, cGeom, r, true, SubGeomFields) ?? [])
            {
                if (DcValue.ObjField(comp, "Geometry") is not { } g || DcValue.ArrField(g, "SubGeometry") is not { } arr) continue;
                foreach (var sub in arr) if (DcValue.StrField(sub, "Tags") is { Length: > 0 } t) subTags.Add(t);
            }
            if (!subTags.Overlaps(atTags)) continue;
            var port = PortFromDataCore(db, cPorts, r)
                ?? PortFromXml(p4k, cVd == null ? null : ShipExport.TryString(db, cVd, r), cMod == null ? null : ShipExport.TryString(db, cMod, r), xmlCache);
            var list = new List<string>();
            if (port != null)
                foreach (var p in paints)
                {
                    if (p.At == null || !subTags.Contains(p.At)) continue;
                    if (!port.RequiredTags.All(p.Tags.Contains)) continue;
                    if (!p.Req.All(t => port.PortTags.Contains(t, StringComparer.Ordinal))) continue;
                    list.Add(p.Name);
                }
            var def = DefaultPaint(db, cLoadout, r);
            string? defPaint = def != null && paintByName.TryGetValue(def, out var dp) && dp.At != null && subTags.Contains(dp.At) ? dp.Name : null;
            if (list.Count == 0 && defPaint == null) continue;
            list = list.OrderBy(p => data.Paints[p].English.Length > 0 ? data.Paints[p].English : p, StringComparer.OrdinalIgnoreCase).ThenBy(p => p, StringComparer.Ordinal).ToList();
            data.Ships[ShortOf(db, r)] = new ShipInfo { Paints = list, DefaultPaint = defPaint };
        }
        return data;
    }

    // DataCore の口 (SItemPortContainerComponentParams.Ports[] の Name が hardpoint_paint のもの)
    private static PortDef? PortFromDataCore(DataCoreDb db, DataCoreDb.CompiledPath? cp, DataCoreDb.Record r)
    {
        if (cp == null) return null;
        foreach (var comp in ShipExport.TryValues(db, cp, r, true, PortFields) ?? [])
        {
            if (DcValue.ArrField(comp, "Ports") is not { } ports) continue;
            foreach (var port in ports)
                if (string.Equals(DcValue.StrField(port, "Name"), PortName, StringComparison.OrdinalIgnoreCase))
                    return new PortDef(Split(DcValue.StrField(port, "PortTags")), Split(DcValue.StrField(port, "RequiredPortTags")));
        }
        return null;
    }

    private static string[] Split(string? s) => (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    // 船の XML の口 (改造の差し替え・値の上書きを当てたもの)
    private static PortDef? PortFromXml(P4kArchive p4k, string? vd, string? mod, Dictionary<string, XElement?> cache)
    {
        if (string.IsNullOrEmpty(vd)) return null;
        var root = Xml(p4k, ShipExport.DataCorePathToP4k(vd), cache);
        if (root == null) return null;
        var part = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "Part"
            && string.Equals((string?)Attr(e, "name"), PortName, StringComparison.OrdinalIgnoreCase)
            && string.Equals((string?)Attr(e, "class"), "ItemPort", StringComparison.OrdinalIgnoreCase));
        var ip = part?.Elements().FirstOrDefault(e => e.Name.LocalName == "ItemPort");
        if (ip == null) return null;
        var a = Attrs(ip);
        if (!string.IsNullOrEmpty(mod))
        {
            var m = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "Modification" && string.Equals((string?)Attr(e, "name"), mod, StringComparison.OrdinalIgnoreCase));
            if (m != null && a.TryGetValue("id", out var id) && id.Length > 0)
            {
                var pf = (string?)Attr(m, "patchFile") ?? "";
                if (pf.Length > 0)
                {
                    var dir = vd.Replace('\\', '/');
                    dir = dir.Contains('/') ? dir[..dir.LastIndexOf('/')] : "";
                    var pr = Xml(p4k, ShipExport.DataCorePathToP4k($"{dir}/{pf}.xml"), cache);
                    var pip = pr?.Descendants().FirstOrDefault(e => e.Name.LocalName == "ItemPort" && (string?)Attr(e, "id") == id);
                    if (pip != null) a = Attrs(pip);
                }
                foreach (var el in m.Descendants().Where(e => e.Name.LocalName == "Elem"))
                {
                    var ea = Attrs(el);
                    if (ea.TryGetValue("idref", out var ir) && ir == id && ea.TryGetValue("name", out var n)) a[n.ToLowerInvariant()] = ea.GetValueOrDefault("value", "");
                }
            }
        }
        return new PortDef(Split(a.GetValueOrDefault("porttags")), Split(a.GetValueOrDefault("requiredtags")));
    }

    private static XAttribute? Attr(XElement e, string name) => e.Attributes().FirstOrDefault(x => string.Equals(x.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
    // 属性 (名前は小文字。同じ名前は後のもの)
    private static Dictionary<string, string> Attrs(XElement e)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var x in e.Attributes()) d[x.Name.LocalName.ToLowerInvariant()] = x.Value;
        return d;
    }

    // XML のファイル (CryXmlB か文字の XML)。読めなければ null
    private static XElement? Xml(P4kArchive p4k, string path, Dictionary<string, XElement?> cache)
    {
        if (cache.TryGetValue(path, out var c)) return c;
        XElement? x = null;
        try
        {
            int e = p4k.Find(path);
            if (e >= 0)
            {
                var d = p4k.Read(e);
                if (CryXmlB.TryParse(d) is { } cx) x = ToElement(cx, cx.Root);
                else x = XElement.Parse(Encoding.UTF8.GetString(d));
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Xml.XmlException or ZstdSharp.ZstdException) { x = null; }
        cache[path] = x;
        return x;
    }

    private static XElement ToElement(CryXmlB x, int node)
    {
        var el = new XElement(XmlName(x.Tag(node)));
        // 同じ名前の属性は後のもの
        foreach (var (k, v) in x.Attributes(node)) el.SetAttributeValue(XmlName(k), v);
        foreach (var c in x.Children(node)) el.Add(ToElement(x, c));
        return el;
    }

    // XML の名前に使えない文字があれば _ にする (CryXmlB のタグ・属性の名前は XML の名前とは限らないため)
    private static string XmlName(string s)
    {
        if (s.Length == 0) return "_";
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            bool ok = i == 0 ? System.Xml.XmlConvert.IsStartNCNameChar(ch) : System.Xml.XmlConvert.IsNCNameChar(ch);
            sb.Append(ok ? ch : '_');
        }
        return sb.ToString();
    }

    // 既定のロードアウトの塗装 (いちばん上の entries のうち itemPortName に paint を含む最初のもので、品が引けるもの。ShipExport.PaintOverride と同じ選び方)
    private static string? DefaultPaint(DataCoreDb db, DataCoreDb.CompiledPath? cp, DataCoreDb.Record r)
    {
        if (cp == null) return null;
        var idx = ShipExport.IndexOf(db);
        foreach (var comp in ShipExport.TryValues(db, cp, r, true, LoadoutFields) ?? [])
        {
            if (DcValue.ObjField(comp, "loadout") is not { } lo || DcValue.ArrField(lo, "entries") is not { } entries) continue;
            foreach (var entry in entries)
            {
                var port = DcValue.StrField(entry, "itemPortName") ?? "";
                if (!port.ToLowerInvariant().Contains("paint", StringComparison.Ordinal)) continue;
                var cls = DcValue.StrField(entry, "entityClassName") ?? "";
                if (cls.Length > 0) { if (idx.Find(cls) != null) return cls; continue; }
                var gid = DcValue.ObjField(entry, "entityClassReference")?.RecordId ?? DcValue.GuidField(entry, "entityClassReference");
                if (gid is { } g && db.RecordById(g) is DataCoreDb.Record rr) { var n = db.String2(rr.NameOffset); return n[(n.LastIndexOf('.') + 1)..]; }
            }
        }
        return null;
    }

    // ゲームの英語の global.ini (Data.p4k の Data\Localization\english\global.ini。キー → 値。キーは大文字小文字を区別しない)。読めなければ空
    private static Dictionary<string, string> EnglishIni(P4kArchive p4k)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            int e = p4k.Find(@"Data\Localization\english\global.ini");
            if (e < 0) return d;
            var text = Encoding.UTF8.GetString(p4k.Read(e));
            // 行の区切りは LF だけ (行末の CR は除く。GlobalIniParser と同じ)
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.EndsWith('\r') ? raw[..^1] : raw;
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                var k = line[..i];
                if (k.Length > 0 && k[0] == '﻿') k = k[1..];
                d[k] = line[(i + 1)..];
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ZstdSharp.ZstdException) { }
        return d;
    }

    // 名前のキー → 日本語名 (translations.db。訳が空・仮の文字なら入れない。GameDataQueryService.ResolveItemNameJa と同じ除き方)
    internal static Dictionary<string, string> JapaneseNames(IEnumerable<string> keys, string translationsDb)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(translationsDb)) return d;
            using var c = new TrackedSqliteConnection($"Data Source={translationsDb};Mode=ReadOnly");
            c.Open();
            foreach (var key in keys.Where(k => k.Length > 0).Distinct(StringComparer.Ordinal))
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT japanese FROM translations WHERE key = @k OR key = @kp ORDER BY (key = @k) DESC LIMIT 1";
                cmd.Parameters.AddWithValue("@k", key);
                cmd.Parameters.AddWithValue("@kp", key + ",P");
                if (cmd.ExecuteScalar() is not string ja) continue;
                ja = ja.Replace(' ', ' ').Trim();
                if (ja.Length == 0 || ja.StartsWith('!') || ja.StartsWith('@') || ja.StartsWith("<=", StringComparison.Ordinal)
                    || ja.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase) || ja.Contains("プレースホルダ")) continue;
                d[key] = ja;
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException) { }
        return d;
    }

    // ===== 保存した塗装 =====

    // その船に保存されている塗装 (品の短い名前)。無ければ null (「標準」)。
    // shipKey: ship_loadouts のキー (保有船 "{pledge_id}|{船名}"・所持船 "my|{id}") が分かればその船の行だけを見る。
    // null なら、塗装の口の行のある船を全部 record に直し (HangarService.PruneLoadouts と同じ直し方)、この船の record の行のうち ship_key の順の最初で、
    // 今この船に付けられる塗装 (ListFor) のものを使う (同じ船を 2 隻以上持っていて違う塗装を保存していれば、最初のものになる)
    public static string? SavedFor(string record, string? shipKey = null) =>
        SavedFor(record, shipKey, DetailPanel.WorkDir(), s => ListFor(s).Select(p => p.Record));

    // workDir: trade_cache.db などの置き場所。allowedOf: 船の短い名前 → 付けられる塗装 (品の短い名前)
    internal static string? SavedFor(string record, string? shipKey, string workDir, Func<string, IEnumerable<string>> allowedOf)
    {
        var shortName = Ship3DExports.ShortName(record);
        var rows = new List<(string Key, string Item)>();
        var db = Path.Combine(workDir, "trade_cache.db");
        var myNames = new Dictionary<int, string>();
        try
        {
            if (!File.Exists(db)) return null;
            using var c = new TrackedSqliteConnection($"Data Source={db};Mode=ReadOnly");
            c.Open();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = shipKey == null
                    ? "SELECT ship_key, item_record FROM ship_loadouts WHERE port_name = @p ORDER BY ship_key"
                    : "SELECT ship_key, item_record FROM ship_loadouts WHERE port_name = @p AND ship_key = @k";
                cmd.Parameters.AddWithValue("@p", PortName);
                if (shipKey != null) cmd.Parameters.AddWithValue("@k", shipKey);
                using var r = cmd.ExecuteReader();
                while (r.Read()) if (!r.IsDBNull(0) && !r.IsDBNull(1) && r.GetString(1).Length > 0) rows.Add((r.GetString(0), r.GetString(1)));
            }
            if (rows.Count == 0) return null;
            if (shipKey == null)
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT id, name FROM my_ships";
                using var r = cmd.ExecuteReader();
                while (r.Read()) if (!r.IsDBNull(0) && !r.IsDBNull(1)) myNames[r.GetInt32(0)] = r.GetString(1);
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
        {
            Log($"保存した塗装を読めませんでした: {ex.Message}");
            return null;
        }
        var allowed = new HashSet<string>(allowedOf(shortName), StringComparer.OrdinalIgnoreCase);
        if (shipKey != null)
        {
            var item = Ship3DExports.ShortName(rows[0].Item);
            return allowed.Contains(item) ? item : null;
        }
        try
        {
            var h = new HangarService();
            h.SetCacheDir(workDir);
            var instances = new Dictionary<string, HangarShipInstance>(StringComparer.Ordinal);
            foreach (var s in h.GetShipInstancesReadOnly()) if (!string.IsNullOrEmpty(s.InstanceKey)) instances.TryAdd(s.InstanceKey, s);
            foreach (var (key, item) in rows)
            {
                string? name = null, mfr = null;
                if (instances.TryGetValue(key, out var inst)) { name = inst.Name; mfr = inst.Matrix?.ManufacturerName; }
                else if (key.StartsWith("my|", StringComparison.Ordinal) && int.TryParse(key[3..], out var id) && myNames.TryGetValue(id, out var mn))
                {
                    name = mn;
                    mfr = h.ResolveShipReadOnly(mn)?.ManufacturerName;
                }
                if (string.IsNullOrEmpty(name)) continue;
                var rec = h.ResolveShipRecordNameReadOnly(name, mfr);
                if (rec == null || !string.Equals(Ship3DExports.ShortName(rec), shortName, StringComparison.OrdinalIgnoreCase)) continue;
                var paint = Ship3DExports.ShortName(item);
                if (allowed.Contains(paint)) return paint;
            }
        }
        catch (Exception ex) { Log($"保存した塗装の船を調べられませんでした: {ex.GetType().FullName}: {ex.Message}"); }
        return null;
    }

    // ===== 材質の表 =====

    // GLB に対する材質の表のファイル (説明はファイルの頭)
    internal static string TablePath(string glbPath, string shortName, string id)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(glbPath)) ?? "";
        if (string.Equals(dir.TrimEnd('\\'), Path.GetFullPath(Ship3DExports.Folder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return Path.Combine(Folder, shortName, SafeName(id) + ".json");
        return Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(glbPath)}_{Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant()}.json");
    }

    private static string SafeName(string id)
    {
        var sb = new StringBuilder();
        foreach (var ch in id) sb.Append(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_');
        return sb.Length == 0 ? "_" : sb.ToString();
    }

    // ===== GLB の材質の決め方の版 =====
    // GLB の asset.extras.generator_version (ShipGlb.GeneratorVersion を書いたもの)。GLB の頭の JSON の塊だけを読み、その中の
    // 「"generator_version":"…"」を探す (JSON を組み立てない)。無ければ null。読んだ結果は GLB の大きさ・更新日時と一緒に覚える
    private static readonly Dictionary<string, (long Len, long Ticks, string? Ver)> GlbVersions = new(StringComparer.OrdinalIgnoreCase);
    internal static string? GlbVersion(string glbPath)
    {
        var fi = new FileInfo(glbPath);
        if (!fi.Exists) return null;
        lock (GlbVersions)
            if (GlbVersions.TryGetValue(fi.FullName, out var c) && c.Len == fi.Length && c.Ticks == fi.LastWriteTimeUtc.Ticks) return c.Ver;
        string? ver = null;
        try
        {
            var json = ReadGlbJson(glbPath);
            var key = "\"generator_version\":\""u8;
            int i = json.AsSpan().IndexOf(key);
            if (i >= 0)
            {
                int s = i + key.Length, e = json.AsSpan(s).IndexOf((byte)'"');
                if (e >= 0) ver = Encoding.UTF8.GetString(json, s, e);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { ver = null; }
        lock (GlbVersions) GlbVersions[fi.FullName] = (fi.Length, fi.LastWriteTimeUtc.Ticks, ver);
        return ver;
    }

    // GLB が今の版 (ShipGlb.GeneratorVersion) と違う (古い・版が無い) 材質の決め方で書かれているか
    public static bool IsOldGlb(string glbPath) => GlbVersion(glbPath) != ShipGlb.GeneratorVersion;

    // ===== 小さな絵 (DetailPanel.ShowItemThumbAsync) =====
    // 絵に当てる材質の表: 色付けを切っていれば色付けなし、古い版の GLB なら標準の表、どちらでもなければ null (GLB の材質のまま。絵の名前は今までどおり GLB の横の .png)
    public static string? ThumbTableId(string glbPath) => !App.Config.Ship3DPalette ? IdNoPalette : IsOldGlb(glbPath) ? IdStandardTable : null;

    // 材質の表を当てた絵のファイル (表と同じ置き場。名前に表と今の版を入れて、色付けあり・なし・版の違う絵を取り違えないようにする)
    public static string ThumbPath(string glbPath, string shortName, string tableId)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(glbPath)) ?? "";
        var tag = $"{tableId}|{ShipGlb.GeneratorVersion}";
        if (string.Equals(dir.TrimEnd('\\'), Path.GetFullPath(Ship3DExports.Folder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return Path.Combine(Folder, shortName, $"thumb_{SafeName(tableId)}_{SafeName(ShipGlb.GeneratorVersion)}.png");
        return Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(glbPath)}_{Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(tag))).ToLowerInvariant()}.png");
    }

    // 船の 3D のキャッシュを消すとき (Ship3DExports): その船の材質の表のフォルダも消す
    internal static void DeleteTables(string shortName)
    {
        try { var d = Path.Combine(Folder, shortName); if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
    }

    // 保存しないで直接読んだ GLB を消すとき (Ship3DExports): その横の材質の表も消す
    internal static void DeleteViewTables(string glbPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(glbPath);
            if (dir == null || !Directory.Exists(dir)) return;
            foreach (var ext in new[] { ".json", ".png" })
                foreach (var f in Directory.GetFiles(dir, Path.GetFileNameWithoutExtension(glbPath) + "_*" + ext)) { try { File.Delete(f); } catch { } }
        }
        catch { }
    }

    private static readonly SemaphoreSlim Gen = new(1, 1);
    private static readonly Dictionary<string, Task<string>> Running = new(StringComparer.OrdinalIgnoreCase);

    // 表示中の GLB (glbPath) に当てる材質の表のファイル。保存したものが今の GLB・Data.p4k のものならそれ、無ければ作る (1 つずつ)。作れなければ例外 (文は利用者に出す)。
    // id: 塗装の品の短い名前・IdNone・IdNoPalette
    public static Task<string> GetTableAsync(string shortName, string glbPath, string id)
    {
        var path = TablePath(glbPath, shortName, id);
        lock (Running)
        {
            if (Running.TryGetValue(path, out var t)) return t;
            t = Task.Run(() => GetTableCoreAsync(shortName, glbPath, id, path));
            Running[path] = t;
            _ = t.ContinueWith(_ => { lock (Running) Running.Remove(path); }, TaskScheduler.Default);
            return t;
        }
    }

    private static async Task<string> GetTableCoreAsync(string shortName, string glbPath, string id, string path)
    {
        var stamp = Ship3DExports.CurrentStamp() ?? throw new InvalidOperationException("ゲームの Data.p4k が見つかりません");
        if (id != IdNone && id != IdNoPalette && id != IdStandardTable && !ListFor(shortName).Any(p => string.Equals(p.Record, id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"「{id}」はこの船に付けられる塗装ではありません");
        await Gen.WaitAsync();
        try
        {
            var glb = new FileInfo(glbPath);
            if (!glb.Exists) throw new InvalidOperationException("3D モデルのファイルがありません");
            switch (ReadFresh(path, glb, stamp.Key, out var unusable))
            {
                case true when unusable != null: throw new PaintUnusableException(unusable);   // 前に「出せない」と分かった塗装 (作り直さずにすぐ返す)
                case true: return path;
            }
            var sw = Stopwatch.StartNew();
            byte[] bytes;
            try { bytes = BuildTable(P4kArchive.Shared(stamp.Path), DataCoreShared.Get(null, stamp.Path), shortName, id, glbPath, stamp.Key); }
            catch (PaintUnusableException ue)
            {
                // 「出せない」ことを表と同じ置き場・同じ鮮度の決まりで覚える (GLB・Data.p4k・版が変われば捨てて調べ直す)。次からは押す前に分かる (UnusableIds)
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllBytes(path, UnusableMarker(shortName, id, glb, stamp.Key, ue.Message));
                }
                catch (Exception wex) { Log($"「{shortName}」の塗装「{id}」を出せないことを保存できませんでした: {wex.Message}"); }
                throw;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
            Log($"「{shortName}」の塗装「{id}」の材質の表を作りました: {sw.Elapsed.TotalSeconds:F1} 秒、{bytes.Length / 1024.0:F0} KB ({path})");
            return path;
        }
        catch (Exception ex)
        {
            Log($"「{shortName}」の塗装「{id}」の材質の表を作れませんでした: {Ship3DExports.Describe(ex)}");
            throw;
        }
        finally { Gen.Release(); }
    }

    // 保存した表が、今の GLB (大きさ・更新日時)・Data.p4k の印・表の版・材質の決め方の版 (ShipGlb.GeneratorVersion) のものか。
    // 「出せない」の印 (UnusableMarker) なら unusable にその理由の文を入れる
    private static bool ReadFresh(string path, FileInfo glb, string p4kKey, out string? unusable)
    {
        unusable = null;
        try
        {
            if (!File.Exists(path)) return false;
            using var fs = File.OpenRead(path);
            using var doc = JsonDocument.Parse(fs);
            var r = doc.RootElement;
            var fresh = r.GetProperty("app").GetString() == AppMark && r.GetProperty("version").GetInt32() == TableVersion
                && r.GetProperty("gen").GetString() == ShipGlb.GeneratorVersion
                && r.GetProperty("p4k").GetString() == p4kKey && r.GetProperty("glbLength").GetInt64() == glb.Length && r.GetProperty("glbTicks").GetInt64() == glb.LastWriteTimeUtc.Ticks;
            if (fresh && r.TryGetProperty("unusable", out var u) && u.ValueKind == JsonValueKind.String) unusable = u.GetString();
            return fresh;
        }
        catch { return false; }
    }

    // 形の照合で出せない塗装 (2026-11-05、利用者の報告「取り直しした方がいいの？」: Origin M80 の Tetra など)。文は利用者に出す
    public sealed class PaintUnusableException(string message) : InvalidOperationException(message);

    // 出せない塗装の印 (表と同じ頭 + "unusable": 理由の文。materials・prims は無い)
    private static byte[] UnusableMarker(string shortName, string id, FileInfo glb, string p4kKey, string reason)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("app", AppMark);
            w.WriteNumber("version", TableVersion);
            w.WriteString("ship", shortName);
            w.WriteString("paint", id);
            w.WriteString("gen", ShipGlb.GeneratorVersion);
            w.WriteString("p4k", p4kKey);
            w.WriteNumber("glbLength", glb.Length);
            w.WriteNumber("glbTicks", glb.LastWriteTimeUtc.Ticks);
            w.WriteString("unusable", reason);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    // この GLB で前に「出せない」と分かった塗装 (id → 理由の文)。印が今の GLB・Data.p4k・版のものだけ
    public static Dictionary<string, string> UnusableIds(string shortName, string glbPath, IEnumerable<string> ids)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var stamp = Ship3DExports.CurrentStamp();
        var glb = new FileInfo(glbPath);
        if (stamp == null || !glb.Exists) return d;
        foreach (var id in ids)
            if (ReadFresh(TablePath(glbPath, shortName, id), glb, stamp.Key, out var u) && u != null) d[id] = u;
        return d;
    }

    // 材質の表を作る (中身は JSON の UTF-8):
    //   { "app", "version", "ship", "paint": id, "p4k": Data.p4k の印, "glbLength", "glbTicks": 当てる GLB の大きさ・更新日時,
    //     "materials": 塗装つきの GLB の materials (glTF の材質の配列そのまま),
    //     "prims": mesh ごとの、primitive ごとの材質の番号 (materials の中の番号。meshes の並び・primitives の並びは今の GLB と同じ) }
    // 今の GLB と形の部分が違えば InvalidOperationException
    internal static byte[] BuildTable(P4kArchive p4k, DataCoreDb db, string shortName, string id, string glbPath, string p4kKey)
    {
        string? paint = id == IdNone ? "" : id == IdNoPalette || id == IdStandardTable ? null : id;
        var (res, json) = ShipExport.ExportJson(p4k, db, shortName, paint, id == IdNoPalette);
        if (!string.Equals(Ship3DExports.ShortName(res.ResolvedName), shortName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"読み取りが別の船 ({res.ResolvedName}) を選びました");
        var glb = new FileInfo(glbPath);
        using var docNew = JsonDocument.Parse(TrimPad(json), new JsonDocumentOptions { MaxDepth = 256 });
        using var docBase = JsonDocument.Parse(TrimPad(ReadGlbJson(glbPath)), new JsonDocumentOptions { MaxDepth = 256 });
        var newShape = ShapeHash(docNew.RootElement);
        if (!newShape.AsSpan().SequenceEqual(ShapeHash(docBase.RootElement)))
        {
            // 理由を分ける: 今のコードの標準 (引数なしの書き出し) の形が、この塗装の形と同じなら、違うのは取り込み済みの GLB の方 (取り込み直せば出せる)。
            // 違えば、この塗装そのものが描く部品 (submesh の出る・出ない) を変える塗装 (取り込み直しても出せない。実例: Origin M80 の Tetra は primitive が 55 個増える)
            bool reimport;
            if (paint == null && id != IdNoPalette) reimport = true;   // 標準の表そのもの
            else
            {
                var (_, stdJson) = ShipExport.ExportJson(p4k, db, shortName, null, false);
                using var docStd = JsonDocument.Parse(TrimPad(stdJson), new JsonDocumentOptions { MaxDepth = 256 });
                reimport = newShape.AsSpan().SequenceEqual(ShapeHash(docStd.RootElement));
            }
            throw new PaintUnusableException(reimport
                ? "今の 3D モデルと形が合わないので、この塗装は出せません。3D モデルを取り込み直すと出せるようになります"
                : "この塗装は色のほかに描く部品も変えるので、今の 3D 表示では出せません (取り込み直しても変わりません)");
        }
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("app", AppMark);
            w.WriteNumber("version", TableVersion);
            w.WriteString("ship", shortName);
            w.WriteString("paint", id);
            w.WriteString("gen", ShipGlb.GeneratorVersion);   // 作ったときの材質の決め方の版 (IsFresh)
            w.WriteString("p4k", p4kKey);
            w.WriteNumber("glbLength", glb.Length);
            w.WriteNumber("glbTicks", glb.LastWriteTimeUtc.Ticks);
            w.WritePropertyName("materials");
            if (docNew.RootElement.TryGetProperty("materials", out var mats)) mats.WriteTo(w); else { w.WriteStartArray(); w.WriteEndArray(); }
            w.WritePropertyName("prims");
            w.WriteStartArray();
            if (docNew.RootElement.TryGetProperty("meshes", out var meshes))
                foreach (var mesh in meshes.EnumerateArray())
                {
                    w.WriteStartArray();
                    if (mesh.TryGetProperty("primitives", out var prims))
                        foreach (var p in prims.EnumerateArray())
                        {
                            if (p.TryGetProperty("material", out var m) && m.TryGetInt32(out var mi)) w.WriteNumberValue(mi); else w.WriteNullValue();
                        }
                    w.WriteEndArray();
                }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    // GLB の JSON の塊 (詰め物の空白を含む)
    internal static byte[] ReadGlbJson(string glbPath)
    {
        using var fs = File.OpenRead(glbPath);
        Span<byte> h = stackalloc byte[20];
        fs.ReadExactly(h);
        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(h) != 0x46546C67 || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(h[16..]) != 0x4E4F534A)
            throw new InvalidDataException("GLB の形ではありません");
        var len = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(h[12..]);
        var json = new byte[len];
        fs.ReadExactly(json);
        return json;
    }

    private static ReadOnlyMemory<byte> TrimPad(byte[] json)
    {
        int n = json.Length;
        while (n > 0 && (json[n - 1] == (byte)' ' || json[n - 1] == 0)) n--;
        return json.AsMemory(0, n);
    }

    // GLB の JSON のうち、形の部分 (asset・materials・extensionsUsed・primitive の material・node の extras を除いたもの) の SHA-256
    internal static byte[] ShapeHash(JsonElement root)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var p in root.EnumerateObject())
            {
                if (p.Name is "asset" or "materials" or "extensionsUsed") continue;
                w.WritePropertyName(p.Name);
                if (p.Name == "meshes" && p.Value.ValueKind == JsonValueKind.Array)
                {
                    w.WriteStartArray();
                    foreach (var mesh in p.Value.EnumerateArray())
                    {
                        w.WriteStartObject();
                        foreach (var mp in mesh.EnumerateObject())
                        {
                            w.WritePropertyName(mp.Name);
                            if (mp.Name != "primitives" || mp.Value.ValueKind != JsonValueKind.Array) { mp.Value.WriteTo(w); continue; }
                            w.WriteStartArray();
                            foreach (var prim in mp.Value.EnumerateArray()) WriteWithout(w, prim, "material");
                            w.WriteEndArray();
                        }
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                else if (p.Name == "nodes" && p.Value.ValueKind == JsonValueKind.Array)
                {
                    w.WriteStartArray();
                    foreach (var node in p.Value.EnumerateArray()) WriteWithout(w, node, "extras");
                    w.WriteEndArray();
                }
                else p.Value.WriteTo(w);
            }
            w.WriteEndObject();
        }
        ms.Position = 0;
        return SHA256.HashData(ms);
    }

    private static void WriteWithout(Utf8JsonWriter w, JsonElement obj, string skip)
    {
        if (obj.ValueKind != JsonValueKind.Object) { obj.WriteTo(w); return; }
        w.WriteStartObject();
        foreach (var p in obj.EnumerateObject())
        {
            if (p.Name == skip) continue;
            p.WriteTo(w);
        }
        w.WriteEndObject();
    }
}
