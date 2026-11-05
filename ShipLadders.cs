using System.Globalization;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

// 船の船内のはしご (ship_ladders。2026-11-05、利用者の指示「はしごを船に足して ironclad で確認して」・「DB 更新してから上げる」)。
// 船の 3D の歩くモード (wwwroot\ship3d\walk.js) が、はしごの箱を作るのに使う。船の 3D モデル (GLB) には入れず、配る DB (gamedata_cache.db) に入れる
// (取り込み済みの船を取り込み直さなくても使えるように)。
//
// 元のデータ: 船の record (EntityClassDefinition) の Components[VehicleComponentParams].objectContainers の socpak (船内の区画) の中の .soc の、
// EntityClass が "Ladder" のエンティティ。船の書き出し (ShipInteriors.Entities) はこの種類を読み飛ばしている (SkipClasses)。
//   実データで確かめた欄 (2026-11-05、DRAK_Ironclad_Assault の hold.socpak 4 つ・rear.socpak 1 つ):
//     <Entity Name="Ladder-000" Pos="10.25,33,-6.125" Rotate="0.70710677,0,0,0.70710677" EntityClass="Ladder" …>
//       <PropertiesDataCore …><Ladder __type="LadderComponentParams" height="5" userIgnoreStaticCollisions="1" approachAngleAtBottom="45" approachAngleAtTop="45">
//   Pos: 区画の中の位置 (はしごの下端)。Rotate: 向き (w, x, y, z)。height: はしごの高さ (m)。上下の範囲は「位置 〜 位置から上向きへ height」。
//   乗り降りの点の座標は欄に無い (DataCore の LadderComponentParams にも座標の欄は無い)。height の欄が無いエンティティは、DataCore の
//   EntityClassDefinition.Ladder の Components[LadderComponentParams].height (既定の値) を使う。
// 座標: 船の座標 (GLB の船の本体と同じ。Z 上・m)。区画の置き方 (objectContainers の Offset の Position・Rotation。ShipInteriors.ContainerTransform と同じ式) を
//   エンティティの位置に当てたもの (GLB では、区画のノード interior_<名前> がいちばん上のノードの直下にあり、その Matrix がこの置き方)。
// 1 行 = はしご 1 つ:
//   ship: 船の record の短い名前 (例 DRAK_Ironclad_Assault。3D のファイル名・ビューアの ship と同じ)。idx: その船の中の順 (0 から)
//   container: 区画 (socpak) の名前 (例 hold)。entity: エンティティの名前 (例 Ladder-000)
//   bx・by・bz: 下端。tx・ty・tz: 上端 (下端からエンティティの上向き (+Z) へ height)。fx・fy・fz: エンティティの +Y 軸の向き (長さ 1)
//   height: はしごの高さ (m)。height_default: height の欄が無く、既定の値を使ったなら 1。approach_bottom・approach_top: 近づける角度 (度。欄が無ければ NULL)
// インデックス構築 (Step 6c。GameDataExtractor.ExtractDeliverableSourcesAsync) で作り直す (1 行も作れなかったときは前の行を残す)。前の版の DB には表が無いので、
// 起動後に一度だけ作る (NeedsOreLocations が表の有無も見る)。画面なしの --rebuild-ore-tables でも同じ処理で作られる
internal static class ShipLadders
{
    public const string Table = "ship_ladders";
    public const string Ddl = """

        CREATE TABLE IF NOT EXISTS ship_ladders (
            ship TEXT NOT NULL,
            idx INTEGER NOT NULL,
            container TEXT NOT NULL,
            entity TEXT NOT NULL,
            bx REAL NOT NULL, by REAL NOT NULL, bz REAL NOT NULL,
            tx REAL NOT NULL, ty REAL NOT NULL, tz REAL NOT NULL,
            fx REAL NOT NULL, fy REAL NOT NULL, fz REAL NOT NULL,
            height REAL NOT NULL,
            height_default INTEGER NOT NULL,
            approach_bottom REAL,
            approach_top REAL,
            PRIMARY KEY (ship, idx)
        );
        """;

    public sealed record Row(string Ship, int Idx, string Container, string Entity, double Bx, double By, double Bz, double Tx, double Ty, double Tz,
        double Fx, double Fy, double Fz, double Height, bool HeightDefault, double? ApproachBottom, double? ApproachTop);

    // socpak の中の Ladder のエンティティ 1 つ (区画の中の座標)
    private sealed record SocLadder(string Name, double[] Pos, double[] Rot, double? Height, double? ApproachBottom, double? ApproachTop);

    // 全部の船のはしごの行を作る。socpak が読めない船・区画は飛ばす (log に知らせる)
    public static List<Row> Find(P4kArchive p4k, DataCoreDb db, CancellationToken ct, Action<string>? log = null)
    {
        var rows = new List<Row>();
        if (db.StructIndexByName("EntityClassDefinition") is not int ecd) return rows;
        var cp = ShipExport.TryCompile(db, ecd, "Components[VehicleComponentParams].objectContainers", DataCoreDb.AnyLeaf);
        if (cp == null) return rows;
        double defaultHeight = DefaultHeight(db, ecd);
        var socCache = new Dictionary<string, List<SocLadder>?>(StringComparer.OrdinalIgnoreCase);
        int bad = 0;
        foreach (var record in db.Records)
        {
            if (record.StructIndex != ecd) continue;
            ct.ThrowIfCancellationRequested();
            var vals = ShipExport.TryValues(db, cp, record, true);
            if (vals == null || vals.Count == 0) continue;
            var name = db.String2(record.NameOffset);
            var ship = name.StartsWith("EntityClassDefinition.", StringComparison.OrdinalIgnoreCase) ? name["EntityClassDefinition.".Length..] : name;
            int idx = 0;
            foreach (var v in vals)
            {
                if (ContainerRef(v) is not { } cref) continue;
                if (!socCache.TryGetValue(cref.File, out var ladders))
                {
                    try { ladders = LoadSocpakLadders(p4k, cref.File); }
                    catch (Exception e) when (ShipExport.IsLoadError(e)) { ladders = null; bad++; }
                    socCache[cref.File] = ladders;
                }
                if (ladders == null || ladders.Count == 0) continue;
                var m = ContainerTransform(cref.Pos, cref.Rot);
                var last = cref.File[(cref.File.LastIndexOfAny(['/', '\\']) + 1)..];
                var container = last.EndsWith(".socpak", StringComparison.OrdinalIgnoreCase) ? last[..^7] : last;
                foreach (var l in ladders)
                {
                    double h = l.Height ?? defaultHeight;
                    if (!(h > 0) || double.IsNaN(h) || double.IsInfinity(h)) continue;   // 高さが決まらないはしごは入れない
                    var up = Rotate(l.Rot, 0, 0, 1);
                    var fwd = Rotate(l.Rot, 0, 1, 0);
                    var b = Apply(m, l.Pos[0], l.Pos[1], l.Pos[2], 1);
                    var t = Apply(m, l.Pos[0] + up[0] * h, l.Pos[1] + up[1] * h, l.Pos[2] + up[2] * h, 1);
                    var f = Apply(m, fwd[0], fwd[1], fwd[2], 0);
                    double fl = Math.Sqrt(f[0] * f[0] + f[1] * f[1] + f[2] * f[2]);
                    if (fl > 1e-9) { f[0] /= fl; f[1] /= fl; f[2] /= fl; }
                    rows.Add(new Row(ship, idx++, container, l.Name, b[0], b[1], b[2], t[0], t[1], t[2], f[0], f[1], f[2], h, l.Height == null, l.ApproachBottom, l.ApproachTop));
                }
            }
        }
        if (bad > 0) log?.Invoke($"[船のはしご] 読めない区画 (socpak) {bad} 件は飛ばしました");
        return rows;
    }

    // DataCore の EntityClassDefinition.Ladder の height (height の欄が無いエンティティに使う既定の値)。引けなければ NaN (その船のはしごは入れない)
    private static double DefaultHeight(DataCoreDb db, int ecd)
    {
        try
        {
            var cp = ShipExport.TryCompile(db, ecd, "Components[LadderComponentParams]", DataCoreDb.AnyLeaf);
            if (cp == null) return double.NaN;
            foreach (var r in db.Records)
            {
                if (r.StructIndex != ecd || db.String2(r.NameOffset) != "EntityClassDefinition.Ladder") continue;
                var vals = ShipExport.TryValues(db, cp, r, true);
                if (vals == null) return double.NaN;
                foreach (var v in vals) if (DcValue.F32Field(v, "height") is float h) return h;
                return double.NaN;
            }
        }
        catch (Exception e) when (ShipExport.IsLoadError(e) || e is DataCoreDb.QueryException) { }
        return double.NaN;
    }

    // objectContainers の 1 つ: socpak のパスと置き方 (ShipInteriors.ContainerRef と同じ読み方)
    private static (string File, float[] Pos, float[] Rot)? ContainerRef(DcValue v)
    {
        if (DcValue.StrField(v, "fileName") is not { Length: > 0 } file) return null;
        float[] pos = [0, 0, 0], rot = [0, 0, 0];
        if (DcValue.ObjField(v, "Offset") is { } off)
        {
            if (DcValue.ObjField(off, "Position") is { } p)
            {
                if (DcValue.F32Field(p, "x") is float x) pos[0] = x;
                if (DcValue.F32Field(p, "y") is float y) pos[1] = y;
                if (DcValue.F32Field(p, "z") is float z) pos[2] = z;
            }
            if (DcValue.ObjField(off, "Rotation") is { } r)
            {
                if (DcValue.F32Field(r, "x") is float x) rot[0] = x;
                if (DcValue.F32Field(r, "y") is float y) rot[1] = y;
                if (DcValue.F32Field(r, "z") is float z) rot[2] = z;
            }
        }
        return (file, pos, rot);
    }

    // 区画の置き方の行列 (列優先。ShipInteriors.ContainerTransform と同じ式 = GLB の interior_<名前> のノードの Matrix)
    private static float[] ContainerTransform(float[] pos, float[] rotDeg)
    {
        float px = ShipInteriors.ToRadians(rotDeg[0]), py = ShipInteriors.ToRadians(rotDeg[1]), pz = ShipInteriors.ToRadians(rotDeg[2]);
        float sx = MathF.Sin(px), cx = MathF.Cos(px), sy = MathF.Sin(py), cy = MathF.Cos(py), sz = MathF.Sin(pz), cz = MathF.Cos(pz);
        return [cy * cz, cy * sz, -sy, 0, sx * sy * cz - cx * sz, sx * sy * sz + cx * cz, sx * cy, 0, cx * sy * cz + sx * sz, cx * sy * sz - sx * cz, cx * cy, 0, pos[0], pos[1], pos[2], 1];
    }

    // 行列 (列優先) を点 (w = 1)・向き (w = 0) に当てる
    private static double[] Apply(float[] m, double x, double y, double z, double w) =>
        [m[0] * x + m[4] * y + m[8] * z + m[12] * w, m[1] * x + m[5] * y + m[9] * z + m[13] * w, m[2] * x + m[6] * y + m[10] * z + m[14] * w];

    // 四元数 (w, x, y, z) で向きを回す
    private static double[] Rotate(double[] q, double vx, double vy, double vz)
    {
        double w = q[0], x = q[1], y = q[2], z = q[3];
        double n = Math.Sqrt(w * w + x * x + y * y + z * z);
        if (n < 1e-12) return [vx, vy, vz];
        w /= n; x /= n; y /= n; z /= n;
        // v' = v + 2 w (q × v) + 2 q × (q × v)
        double cx = y * vz - z * vy, cy = z * vx - x * vz, cz = x * vy - y * vx;
        double dx = y * cz - z * cy, dy = z * cx - x * cz, dz = x * cy - y * cx;
        return [vx + 2 * (w * cx + dx), vy + 2 * (w * cy + dy), vz + 2 * (w * cz + dz)];
    }

    // socpak の中の .soc から Ladder のエンティティを集める (読み方は ShipInteriors.LoadSocpak・ParseSoc・Entities と同じ: エンティティの入れ物の直下の Entity だけ)
    private static List<SocLadder> LoadSocpakLadders(P4kArchive p4k, string socpakPath)
    {
        var res = new List<SocLadder>();
        var norm = socpakPath.Replace('/', '\\');
        var p4kPath = norm.ToLowerInvariant().StartsWith(@"data\", StringComparison.Ordinal) ? norm : @"Data\" + norm;
        int e = p4k.Find(p4kPath);
        if (e < 0) throw new InvalidDataException($"socpak not found: {p4kPath}");
        var inner = P4kArchive.FromBytes(p4k.Read(e));
        for (int i = 0; i < inner.Count; i++)
        {
            if (!inner.Name(i).ToLowerInvariant().EndsWith(".soc", StringComparison.Ordinal)) continue;
            byte[] soc;
            try { soc = inner.Read(i); }
            catch (Exception ex) when (ShipExport.IsLoadError(ex)) { continue; }
            CryChunkFile cf;
            try { cf = CryChunkFile.Parse(soc); }
            catch (InvalidDataException) { continue; }
            if (cf.IsIvo) continue;
            foreach (var c in cf.Chunks)
            {
                if (c.Type != CryChunkFile.CrChCryXmlB) continue;
                CryXmlB x;
                try { x = CryXmlB.Parse(cf.ChunkBytes(c)); }
                catch (InvalidDataException) { continue; }
                int root = x.Root, container = -1;
                var rootTag = x.Tag(root);
                foreach (var ch in x.Children(root)) { var t = x.Tag(ch); if (t == "Entities" || t == "SCOC_Entities") { container = ch; break; } }
                if (container < 0 && (rootTag == "Entities" || rootTag == "SCOC_Entities")) container = root;
                if (container < 0) continue;
                foreach (var ent in x.Children(container))
                {
                    if (x.Tag(ent) != "Entity") continue;
                    var attrs = x.AttributeMap(ent);
                    if (attrs.GetValueOrDefault("EntityClass", "") != "Ladder") continue;
                    var pos = Csv(attrs.GetValueOrDefault("Pos", "0,0,0"), [0, 0, 0]);
                    var rot = Csv(attrs.GetValueOrDefault("Rotate", "1,0,0,0"), [1, 0, 0, 0]);
                    double? height = null, ab = null, at = null;
                    foreach (var pc in x.Children(ent))
                    {
                        if (x.Tag(pc) != "PropertiesDataCore") continue;
                        foreach (var lc in x.Children(pc))
                        {
                            if (x.Tag(lc) != "Ladder") continue;
                            var la = x.AttributeMap(lc);
                            height = Num(la, "height"); ab = Num(la, "approachAngleAtBottom"); at = Num(la, "approachAngleAtTop");
                        }
                    }
                    res.Add(new SocLadder(attrs.GetValueOrDefault("Name", "Ladder"), pos, rot, height, ab, at));
                }
            }
        }
        return res;
    }

    private static double? Num(Dictionary<string, string> a, string key) =>
        a.TryGetValue(key, out var s) && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && !double.IsNaN(v) && !double.IsInfinity(v) ? v : null;

    // "a,b,c" を数の並びに (足りない所・読めない所は既定の値)
    private static double[] Csv(string s, double[] def)
    {
        var res = (double[])def.Clone();
        var parts = s.Split(',');
        for (int i = 0; i < res.Length && i < parts.Length; i++)
            if (double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && !double.IsNaN(v) && !double.IsInfinity(v)) res[i] = v;
        return res;
    }

    // ship_ladders を入れ替える (表が無ければ作る)
    public static void Write(SqliteConnection db, List<Row> rows)
    {
        using var tx = db.BeginTransaction();
        using (var ddl = db.CreateCommand())
        {
            ddl.Transaction = tx;
            ddl.CommandText = Ddl + "DELETE FROM ship_ladders;";
            ddl.ExecuteNonQuery();
        }
        using var ins = db.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = "INSERT INTO ship_ladders(ship,idx,container,entity,bx,by,bz,tx,ty,tz,fx,fy,fz,height,height_default,approach_bottom,approach_top) "
            + "VALUES(@ship,@idx,@c,@e,@bx,@by,@bz,@tx,@ty,@tz,@fx,@fy,@fz,@h,@hd,@ab,@at)";
        var pShip = ins.Parameters.Add("@ship", SqliteType.Text);
        var pIdx = ins.Parameters.Add("@idx", SqliteType.Integer);
        var pC = ins.Parameters.Add("@c", SqliteType.Text);
        var pE = ins.Parameters.Add("@e", SqliteType.Text);
        var p = new[] { "@bx", "@by", "@bz", "@tx", "@ty", "@tz", "@fx", "@fy", "@fz", "@h" }.Select(n => ins.Parameters.Add(n, SqliteType.Real)).ToArray();
        var pHd = ins.Parameters.Add("@hd", SqliteType.Integer);
        var pAb = ins.Parameters.Add("@ab", SqliteType.Real);
        var pAt = ins.Parameters.Add("@at", SqliteType.Real);
        foreach (var r in rows)
        {
            pShip.Value = r.Ship; pIdx.Value = r.Idx; pC.Value = r.Container; pE.Value = r.Entity;
            double[] v = [r.Bx, r.By, r.Bz, r.Tx, r.Ty, r.Tz, r.Fx, r.Fy, r.Fz, r.Height];
            for (int i = 0; i < v.Length; i++) p[i].Value = v[i];
            pHd.Value = r.HeightDefault ? 1 : 0;
            pAb.Value = r.ApproachBottom is double ab ? ab : DBNull.Value;
            pAt.Value = r.ApproachTop is double at ? at : DBNull.Value;
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // その船のはしごの行 (ビューアへ渡す)。DB が無い・表が無い (前の版の DB)・読めないときは空 (ビューアは今までどおり形の名前からだけはしごを作る)
    public static List<Row> ReadFor(string gamedataDbPath, string shortName)
    {
        var res = new List<Row>();
        try
        {
            if (!File.Exists(gamedataDbPath)) return res;
            using var c = new SqliteConnection($"Data Source={gamedataDbPath};Mode=ReadOnly;Pooling=False");
            c.Open();
            using (var has = c.CreateCommand())
            {
                has.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @t";
                has.Parameters.AddWithValue("@t", Table);
                if (Convert.ToInt64(has.ExecuteScalar() ?? 0L) == 0) return res;
            }
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT idx,container,entity,bx,by,bz,tx,ty,tz,fx,fy,fz,height,height_default,approach_bottom,approach_top FROM ship_ladders WHERE ship = @s COLLATE NOCASE ORDER BY idx";
            cmd.Parameters.AddWithValue("@s", shortName);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                res.Add(new Row(shortName, r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5), r.GetDouble(6), r.GetDouble(7), r.GetDouble(8),
                    r.GetDouble(9), r.GetDouble(10), r.GetDouble(11), r.GetDouble(12), r.GetInt32(13) != 0, r.IsDBNull(14) ? null : r.GetDouble(14), r.IsDBNull(15) ? null : r.GetDouble(15)));
        }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException) { res.Clear(); }
        return res;
    }
}
