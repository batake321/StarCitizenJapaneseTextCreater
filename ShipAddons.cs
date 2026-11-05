using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

// 船の 3D に足す区画 (ship_addons。2026-11-05、利用者の報告「床がない。船舶は表示しない部品があったらダメだよね」)。ShipExportAddon.cs の頭の説明の 2 種類の区画
// (取り付けられた子の乗り物が自分で持つ区画・エレベーターやリフトのかごの区画) を、船ごとに並べた表と、前の版で取り込んだ船にそれを足す道。
//
// 表 ship_addons (配る DB gamedata_cache.db。作り直しの道・判定・0 行のときの扱い・バックアップの扱いは ship_ladders・ship_transit と同じ: GameDataExtractor.WriteShipAddons):
//   1 行 = 足す区画 1 つ。ship: 船の record の短い名前。idx: その船の中の順 (0 から)
//   kind: 'child' (子の乗り物の区画) か 'carriage' (かごの区画)。owner: 子の品の名前 (child)、かごを指す管理のある区画の名前 (carriage。子の乗り物の中なら「子の名前:区画の名前」)。
//   socpak: その区画のファイル。
//   使い道は「この船に足す区画があるか」を、船を開いたときに Data.p4k を読まずに決めること (Count)。足す区画の中身と置き方は、作るときに船の書き出しと同じ処理で決める
//   (ShipExport.ExportAddon。この表の行は使わない)。
//
// 前の版で取り込んだ船に足す道 (GetAsync。ビューアの addonInit → Ship3DWeb):
//   当たる船: 表示している GLB が置き場所の取り込み済みの船で、横の記録の ShipExportVersion が、区画を初めから入れるようになった版 (Ship3DExports.ShipExtrasVersion) より古く、
//     この表にその船の行があるもの。新しい版で取り込んだ船・直接読んだ船 (保存しない設定) は初めから入っているので当たらない。
//   作る物: 足りない区画だけの GLB (船の本体は取り込み直さない)。置き場所は塗装の材質の表と同じ船ごとのフォルダ (ShipPaints.Folder\<短い名前>\addon.glb。色付けを切っているときは
//     addon_np.glb) と、その横の記録 addon.glb.json (アプリの印・形の版・Data.p4k の印・船の GLB の大きさと更新日時)。記録が今のものならそれを使い、違えば作り直す。
//     船を取り込み直した・キャッシュを消したときは、塗装の材質の表と一緒に消える (ShipPaints.DeleteTables)。最大容量には数えない。
//     「3D モデルを保存しない」の設定のときは、作業用のフォルダに作って表示の間だけ置く (表示をやめたら消す。ReleaseViewFiles)。
//   見た目: 色は船の書き出しと同じ (本体のパレット + 既定のロードアウトの塗装)。色付けを切っていれば色付けなし。ガラスの色は今の決まり。
//     塗装を切り替えたときの色は、足した区画には当たらない (塗装の材質の表は船の GLB の部品の番号で引くため。足した区画は既定の塗装の色のまま)
internal static class ShipAddons
{
    public const string Table = "ship_addons";
    public const string Ddl = """

        CREATE TABLE IF NOT EXISTS ship_addons (
            ship TEXT NOT NULL,
            idx INTEGER NOT NULL,
            kind TEXT NOT NULL,
            owner TEXT NOT NULL,
            socpak TEXT NOT NULL,
            PRIMARY KEY (ship, idx)
        );
        """;

    public sealed record Row(string Ship, int Idx, string Kind, string Owner, string Socpak);

    // 全部の船 (自分の区画の一覧を持つ record) の、足す区画の行を作る。読めない区画は飛ばす (log に知らせる)
    public static List<Row> Find(P4kArchive p4k, DataCoreDb db, CancellationToken ct, Action<string>? log = null)
    {
        var rows = new List<Row>();
        if (db.StructIndexByName("EntityClassDefinition") is not int ecd) return rows;
        var cp = ShipExport.TryCompile(db, ecd, "Components[VehicleComponentParams].objectContainers", DataCoreDb.AnyLeaf);
        if (cp == null) return rows;
        var idx = ShipExport.IndexOf(db);
        var carriageCache = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        int bad = 0;
        // 区画 (socpak) の中の管理が指すかごの区画
        List<string> Carriages(string file)
        {
            if (carriageCache.TryGetValue(file, out var l)) return l;
            l = ShipInteriors.CarriageRefs(p4k, file, _ => bad++).Select(c => c.File).ToList();
            return carriageCache[file] = l;
        }
        static string ContainerName(string file)
        {
            var last = file[(file.LastIndexOfAny(['/', '\\']) + 1)..];
            return last.EndsWith(".socpak", StringComparison.OrdinalIgnoreCase) ? last[..^7] : last;
        }
        List<(string File, float[] Pos, float[] Rot)> Refs(DataCoreDb.Record r)
        {
            var res = new List<(string, float[], float[])>();
            var vals = ShipExport.TryValues(db, cp, r, true);
            if (vals != null) foreach (var v in vals) if (ShipLadders.ContainerRef(v) is { } c) res.Add(c);
            return res;
        }
        foreach (var record in db.Records)
        {
            if (record.StructIndex != ecd) continue;
            ct.ThrowIfCancellationRequested();
            var refs = Refs(record);
            if (refs.Count == 0) continue;
            var name = db.String2(record.NameOffset);
            var ship = name.StartsWith("EntityClassDefinition.", StringComparison.OrdinalIgnoreCase) ? name["EntityClassDefinition.".Length..] : name;
            int i = 0;
            // 子の乗り物の区画 (と、その中のかご)
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var n in ShipExport.LoadoutFlat(idx, record))
            {
                if (!seen.Add(n.EntityName)) continue;
                var crefs = Refs(n.Record);
                foreach (var c in crefs) rows.Add(new Row(ship, i++, "child", n.EntityName, c.File));
                foreach (var c in crefs)
                    foreach (var cf in Carriages(c.File)) rows.Add(new Row(ship, i++, "carriage", $"{n.EntityName}:{ContainerName(c.File)}", cf));
            }
            // 船そのものの区画のかご
            foreach (var c in refs)
                foreach (var cf in Carriages(c.File)) rows.Add(new Row(ship, i++, "carriage", ContainerName(c.File), cf));
        }
        if (bad > 0) log?.Invoke($"[船に足す区画] 読めない区画 (socpak) {bad} 件は飛ばしました");
        return rows;
    }

    // ship_addons を入れ替える (表が無ければ作る)
    public static void Write(SqliteConnection db, List<Row> rows)
    {
        using var tx = db.BeginTransaction();
        using (var ddl = db.CreateCommand())
        {
            ddl.Transaction = tx;
            ddl.CommandText = Ddl + "DELETE FROM ship_addons;";
            ddl.ExecuteNonQuery();
        }
        using var ins = db.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = "INSERT INTO ship_addons(ship,idx,kind,owner,socpak) VALUES(@ship,@idx,@k,@o,@s)";
        var pShip = ins.Parameters.Add("@ship", SqliteType.Text);
        var pIdx = ins.Parameters.Add("@idx", SqliteType.Integer);
        var pK = ins.Parameters.Add("@k", SqliteType.Text);
        var pO = ins.Parameters.Add("@o", SqliteType.Text);
        var pS = ins.Parameters.Add("@s", SqliteType.Text);
        foreach (var r in rows)
        {
            pShip.Value = r.Ship; pIdx.Value = r.Idx; pK.Value = r.Kind; pO.Value = r.Owner; pS.Value = r.Socpak;
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // その船の足す区画の数。DB が無い・表が無い (前の版の DB)・読めないときは 0 (何も足さない。船は今までどおり)
    public static int Count(string gamedataDbPath, string shortName)
    {
        try
        {
            if (!File.Exists(gamedataDbPath)) return 0;
            using var c = new SqliteConnection($"Data Source={gamedataDbPath};Mode=ReadOnly;Pooling=False");
            c.Open();
            using (var has = c.CreateCommand())
            {
                has.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @t";
                has.Parameters.AddWithValue("@t", Table);
                if (Convert.ToInt64(has.ExecuteScalar() ?? 0L) == 0) return 0;
            }
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM ship_addons WHERE ship = @s COLLATE NOCASE";
            cmd.Parameters.AddWithValue("@s", shortName);
            return (int)Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException) { return 0; }
    }

    // ===== 前の版で取り込んだ船に足す GLB =====

    // 足す GLB の形の版 (作り方を変えたら進める。横の記録の版が違えば作り直す)
    private const int FormatVersion = 1;
    private const string Mark = "StarCitizenJapaneseTextCreater.ship3d.addon";

    private sealed class Meta
    {
        public string App { get; set; } = "";
        public int Version { get; set; }
        public string P4k { get; set; } = "";
        public long GlbLength { get; set; }
        public long GlbTicks { get; set; }
        public List<string> Sections { get; set; } = new();
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Task<string?>> Running = new(StringComparer.OrdinalIgnoreCase);
    // 保存しない設定で作った GLB (鍵 → 作業用のフォルダの中のファイル)。表示をやめたら消す
    private static readonly Dictionary<string, string> ViewFiles = new(StringComparer.OrdinalIgnoreCase);

    // 表示している船の GLB (glbPath) に重ねる、足りない区画だけの GLB のパス。足す物が無い (当たらない船) なら null。作れなければ例外。
    // onBusy: これから作る (時間がかかる) ときに 1 回呼ぶ (ビューアに進み具合を出させる)。UI スレッドから呼ぶ
    public static Task<string?> GetAsync(string shortName, string glbPath, Action onBusy)
    {
        if (!Ship3DExports.IsOldShipGlb(shortName, glbPath)) return Task.FromResult<string?>(null);
        var noPalette = !App.Config.Ship3DPalette;
        var noSave = Ship3DExports.NoSave;
        var dbPath = Path.Combine(App.Config.WorkingDirectory, "gamedata_cache.db");
        var key = $"{shortName}|{(noPalette ? "np" : "pal")}";
        lock (Gate)
        {
            if (Running.TryGetValue(key, out var t)) return t;
            t = Task.Run(() => GetCore(shortName, glbPath, dbPath, noPalette, noSave, key, onBusy));
            Running[key] = t;
            _ = t.ContinueWith(_ => { lock (Gate) Running.Remove(key); }, TaskScheduler.Default);
            return t;
        }
    }

    private static string? GetCore(string shortName, string glbPath, string dbPath, bool noPalette, bool noSave, string key, Action onBusy)
    {
        if (Count(dbPath, shortName) == 0) return null;
        var stamp = Ship3DExports.CurrentStamp();
        var glb = new FileInfo(glbPath);
        if (stamp == null || !glb.Exists) return null;
        if (noSave)
        {
            lock (Gate) if (ViewFiles.TryGetValue(key, out var vf) && File.Exists(vf)) return vf;
        }
        var dir = Path.Combine(ShipPaints.Folder, shortName);
        var final = Path.Combine(dir, noPalette ? "addon_np.glb" : "addon.glb");
        var metaPath = final + ".json";
        if (!noSave && File.Exists(final) && ReadMeta(metaPath) is { } m && m.App == Mark && m.Version == FormatVersion && m.P4k == stamp.Key
            && m.GlbLength == glb.Length && m.GlbTicks == glb.LastWriteTimeUtc.Ticks) return final;
        onBusy();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Directory.CreateDirectory(Ship3DExports.WorkFolder);
        var tmp = Ship3DExports.NewWorkFile(shortName + "_addon", ".glb");
        try
        {
            var sections = ShipExport.ExportAddon(P4kArchive.Shared(stamp.Path), DataCoreShared.Get(null, stamp.Path), shortName, glbPath, tmp, noPalette);
            var bytes = new FileInfo(tmp).Length;
            string result;
            if (noSave)
            {
                lock (Gate) ViewFiles[key] = tmp;
                result = tmp;
            }
            else
            {
                Directory.CreateDirectory(dir);
                File.Move(tmp, final, overwrite: true);
                File.WriteAllText(metaPath, JsonSerializer.Serialize(new Meta { App = Mark, Version = FormatVersion, P4k = stamp.Key, GlbLength = glb.Length, GlbTicks = glb.LastWriteTimeUtc.Ticks, Sections = sections }));
                result = final;
            }
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[3D モデル] 「{shortName}」は前の版で取り込んだ船なので、入っていなかった区画 {sections.Count} つ ({string.Join("・", sections)}) だけを取り出して足します" +
                $" ({sw.Elapsed.TotalSeconds:F1} 秒、{bytes / 1048576.0:F0} MB{(noSave ? "、保存しない設定のため表示の間だけ置きます" : "")}。船の本体は取り込み直していません)");
            return result;
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    private static Meta? ReadMeta(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Meta>(File.ReadAllText(path)) : null; }
        catch { return null; }
    }

    // その船の表示が無くなったとき (Ship3DExports.ReleaseView): 保存しない設定で作った GLB を消す
    public static void ReleaseViewFiles(string shortName)
    {
        List<string> files;
        lock (Gate)
        {
            var keys = ViewFiles.Keys.Where(k => k.StartsWith(shortName + "|", StringComparison.OrdinalIgnoreCase)).ToList();
            files = keys.Select(k => ViewFiles[k]).ToList();
            foreach (var k in keys) ViewFiles.Remove(k);
        }
        foreach (var f in files) { try { File.Delete(f); } catch { } }
    }
}
