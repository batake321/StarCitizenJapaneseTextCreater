using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

// 船の船内のエレベーター・リフトの乗り場 (ship_transit。2026-11-05、利用者の指示「館内エレベーターも場所のエレベーターと同じように リフトも場所のエレベーターと同じように」)。
// 船の 3D の歩くモード (wwwroot\ship3d\walk.js) が、ステーションの 3D のエレベーター (wwwroot\stationmap\view3d.js の showElev・elevMove・elevPlace) と同じ使い方で
// 階の間を移るのに使う。船の 3D モデル (GLB) には入れず、配る DB (gamedata_cache.db) に入れる (はしごの表 ship_ladders と同じ。取り込み済みの船を取り込み直さなくても使える)。
//
// 元のデータ: 船の record の objectContainers の socpak (船内の区画) の中の .soc の、EntityClass が "TransitManager"・"TransitGateway" のエンティティ
// (船の書き出し ShipInteriors.Entities はこの種類を読み飛ばしている)。エレベーターもリフトも、ステーションと同じ Transit の仕組み。
//   実データで確かめた欄 (2026-11-05、DRAK_Ironclad の transit_elevator_shaft.socpak の TransitManager_Elevator (行き先 4)・hold.socpak の TransitManager_Cargo_Lift_Left・_Right (行き先 2 ずつ)):
//     <Entity Name="TransitManager_Elevator" Pos="-15.75,-14.125,1.75" EntityClass="TransitManager" …>
//       <PropertiesDataCore …><SCTransitManager carriageDisplayTypeId="@transit_carriage_elevator" …>
//       <SCTransitManager …><TransitDestinations>
//         <Destination nameIdentifier="@vehicle_deck_cargo_hold" destinationPos="1.78125,0.03125,-6.375" …>
//           <Gateway Entity="4b27ee43-…" gatewayPos="1.65625,0,-6.65625" gatewayQuat="0,0,0,1" safeTeleportOffset="-3,0,-0.5" …>
//             (Entity: 乗り場のエンティティの EntityCryGUID。gatewayPos・gatewayQuat: 管理のエンティティからの位置と向き。safeTeleportOffset: 乗り場からの、人を安全に置く所)
//     <Entity Name="TransitGateway_Elevator_Rear_Hold" Pos="-14.09375,-14.125,-4.90625" Rotate="0,0,0,1" EntityClass="TransitGateway" EntityCryGUID="4b27ee43-…" …>
//   管理 (TransitManager) の TransitDestinations に、行き先の名前の鍵 (nameIdentifier) と、その乗り場 (Gateway) が直接書かれているので、
//   路線 = 管理 1 つ、乗り場 = その Gateway、乗り場の行き先の名前 = その Destination の nameIdentifier とする
//   (ステーションは区画の metadata のタグといちばん近い行き先から同じ物を決める: StationRouteGraph.TransitLines・TransitList)。
//   乗り場の位置・向きは、乗り場のエンティティの Pos・Rotate (同じ .soc に EntityCryGUID が同じ TransitGateway があるとき)。無ければ管理の Pos・Rotate に gatewayPos・gatewayQuat を重ねる。
//   降りる所 (sx・sy・sz) は、乗り場の位置 + 乗り場の向きで回した safeTeleportOffset。船の 3D モデルにはエレベーターの扉・かごの形が入っていないので、
//   ビューアは、移植元の「行き先の扉の前に立つ」が当たらないとき、この降りる所の真下の床に立つ
//   (Ironclad で確かめた値: 貨物室のリフトの上の乗り場 (−9.32, 35.93, −0.28) → 降りる所 (−11.57, 35.93, −0.28) = 通路の台の上)。
// 座標: 船の座標 (GLB の船の本体と同じ。Z 上・m)。区画の置き方を当てたもの (ShipLadders と同じ)。
// 1 行 = 乗り場 1 つ:
//   ship: 船の record の短い名前。idx: その船の中の順 (0 から)
//   kind: 'elevator' (ビューアへ渡す種類。ステーションの乗り場の種類 elevator に当たる)。carriage: 管理の carriageDisplayTypeId (例 @transit_carriage_elevator。無ければ '')
//   container: 区画 (socpak) の名前。manager: 管理のエンティティの名前。gateway: 乗り場のエンティティの名前 (見つからなければ '')
//   x・y・z: 乗り場の位置。sx・sy・sz: 降りる所 (safeTeleportOffset の欄が無ければ NULL)。line: 路線の鍵 (船の中の区画の順:区画の名前:管理の名前)。
//   stop: 行き先の名前の鍵 (例 @vehicle_deck_cargo_hold)
// 作り直しの道・判定・0 行のときの扱い・バックアップの扱いは ship_ladders と同じ (GameDataExtractor.WriteShipTransit)
internal static class ShipTransit
{
    public const string Table = "ship_transit";
    public const string Ddl = """

        CREATE TABLE IF NOT EXISTS ship_transit (
            ship TEXT NOT NULL,
            idx INTEGER NOT NULL,
            kind TEXT NOT NULL,
            carriage TEXT NOT NULL,
            container TEXT NOT NULL,
            manager TEXT NOT NULL,
            gateway TEXT NOT NULL,
            x REAL NOT NULL, y REAL NOT NULL, z REAL NOT NULL,
            sx REAL, sy REAL, sz REAL,
            line TEXT NOT NULL,
            stop TEXT NOT NULL,
            PRIMARY KEY (ship, idx)
        );
        """;

    public sealed record Row(string Ship, int Idx, string Kind, string Carriage, string Container, string Manager, string Gateway, double X, double Y, double Z,
        double? Sx, double? Sy, double? Sz, string Line, string Stop);

    // socpak の中の乗り場 1 つ (区画の中の座標)。Safe: 降りる所 (無ければ null)
    private sealed record SocStop(string Manager, string Carriage, string Gateway, double[] Pos, double[]? Safe, string Stop);

    // 全部の船の乗り場の行を作る。socpak が読めない区画は飛ばす (log に知らせる)
    public static List<Row> Find(P4kArchive p4k, DataCoreDb db, CancellationToken ct, Action<string>? log = null)
    {
        var rows = new List<Row>();
        if (db.StructIndexByName("EntityClassDefinition") is not int ecd) return rows;
        var cp = ShipExport.TryCompile(db, ecd, "Components[VehicleComponentParams].objectContainers", DataCoreDb.AnyLeaf);
        if (cp == null) return rows;
        var socCache = new Dictionary<string, List<SocStop>?>(StringComparer.OrdinalIgnoreCase);
        int bad = 0;
        foreach (var record in db.Records)
        {
            if (record.StructIndex != ecd) continue;
            ct.ThrowIfCancellationRequested();
            var vals = ShipExport.TryValues(db, cp, record, true);
            if (vals == null || vals.Count == 0) continue;
            var name = db.String2(record.NameOffset);
            var ship = name.StartsWith("EntityClassDefinition.", StringComparison.OrdinalIgnoreCase) ? name["EntityClassDefinition.".Length..] : name;
            int idx = 0, ci = -1;
            foreach (var v in vals)
            {
                if (ShipLadders.ContainerRef(v) is not { } cref) continue;
                ci++;
                if (!socCache.TryGetValue(cref.File, out var stops))
                {
                    try { stops = LoadSocpakStops(p4k, cref.File); }
                    catch (Exception e) when (ShipExport.IsLoadError(e)) { stops = null; bad++; }
                    socCache[cref.File] = stops;
                }
                if (stops == null || stops.Count == 0) continue;
                var m = ShipLadders.ContainerTransform(cref.Pos, cref.Rot);
                var last = cref.File[(cref.File.LastIndexOfAny(['/', '\\']) + 1)..];
                var container = last.EndsWith(".socpak", StringComparison.OrdinalIgnoreCase) ? last[..^7] : last;
                foreach (var s in stops)
                {
                    var p = ShipLadders.Apply(m, s.Pos[0], s.Pos[1], s.Pos[2], 1);
                    var sp = s.Safe != null ? ShipLadders.Apply(m, s.Safe[0], s.Safe[1], s.Safe[2], 1) : null;
                    rows.Add(new Row(ship, idx++, "elevator", s.Carriage, container, s.Manager, s.Gateway, p[0], p[1], p[2], sp?[0], sp?[1], sp?[2], $"{ci}:{container}:{s.Manager}", s.Stop));
                }
            }
        }
        if (bad > 0) log?.Invoke($"[船のエレベーター] 読めない区画 (socpak) {bad} 件は飛ばしました");
        return rows;
    }

    // 四元数 (w, x, y, z) の積 a × b (b を先に、a を後に回す)
    private static double[] QuatMul(double[] a, double[] b) =>
    [
        a[0] * b[0] - a[1] * b[1] - a[2] * b[2] - a[3] * b[3],
        a[0] * b[1] + a[1] * b[0] + a[2] * b[3] - a[3] * b[2],
        a[0] * b[2] - a[1] * b[3] + a[2] * b[0] + a[3] * b[1],
        a[0] * b[3] + a[1] * b[2] - a[2] * b[1] + a[3] * b[0],
    ];

    // socpak の中の管理 (TransitManager) の行き先と乗り場を集める
    private static List<SocStop> LoadSocpakStops(P4kArchive p4k, string socpakPath)
    {
        var res = new List<SocStop>();
        // 乗り場のエンティティ (EntityCryGUID → 名前・位置・向き)。管理より後ろに書かれていることがあるので、先に全部集める
        var gateways = new Dictionary<string, (string Name, double[] Pos, double[] Rot)>(StringComparer.OrdinalIgnoreCase);
        var managers = new List<(string Name, string Carriage, double[] Pos, double[] Rot, List<(string Stop, string Guid, double[]? Rel, double[] Quat, double[]? Safe)> Stops)>();
        ShipLadders.ForEachEntity(p4k, socpakPath, (x, ent, attrs) =>
        {
            var cls = attrs.GetValueOrDefault("EntityClass", "");
            if (cls == "TransitGateway")
            {
                if (attrs.TryGetValue("EntityCryGUID", out var g) && g.Length > 0)
                    gateways[g] = (attrs.GetValueOrDefault("Name", ""), ShipLadders.Csv(attrs.GetValueOrDefault("Pos", "0,0,0"), [0, 0, 0]),
                        ShipLadders.Csv(attrs.GetValueOrDefault("Rotate", "1,0,0,0"), [1, 0, 0, 0]));
                return;
            }
            if (cls != "TransitManager") return;
            string carriage = "";
            var stops = new List<(string Stop, string Guid, double[]? Rel, double[] Quat, double[]? Safe)>();
            foreach (var c in x.Children(ent))
            {
                var tag = x.Tag(c);
                if (tag == "PropertiesDataCore")
                {
                    foreach (var pc in x.Children(c))
                        if (x.Tag(pc) == "SCTransitManager" && x.AttributeMap(pc).TryGetValue("carriageDisplayTypeId", out var cd)) carriage = cd;
                }
                else if (tag == "SCTransitManager")
                {
                    foreach (var td in x.Children(c))
                    {
                        if (x.Tag(td) != "TransitDestinations") continue;
                        foreach (var d in x.Children(td))
                        {
                            if (x.Tag(d) != "Destination") continue;
                            var stop = x.AttributeMap(d).GetValueOrDefault("nameIdentifier", "");
                            foreach (var gw in x.Children(d))
                            {
                                if (x.Tag(gw) != "Gateway") continue;
                                var ga = x.AttributeMap(gw);
                                stops.Add((stop, ga.GetValueOrDefault("Entity", ""), ga.TryGetValue("gatewayPos", out var gp) ? ShipLadders.Csv(gp, [0, 0, 0]) : null,
                                    ShipLadders.Csv(ga.GetValueOrDefault("gatewayQuat", "1,0,0,0"), [1, 0, 0, 0]),
                                    ga.TryGetValue("safeTeleportOffset", out var so) ? ShipLadders.Csv(so, [0, 0, 0]) : null));
                            }
                        }
                    }
                }
            }
            if (stops.Count > 0)
                managers.Add((attrs.GetValueOrDefault("Name", "TransitManager"), carriage, ShipLadders.Csv(attrs.GetValueOrDefault("Pos", "0,0,0"), [0, 0, 0]),
                    ShipLadders.Csv(attrs.GetValueOrDefault("Rotate", "1,0,0,0"), [1, 0, 0, 0]), stops));
        });
        foreach (var mg in managers)
            foreach (var (stop, guid, rel, quat, safeOff) in mg.Stops)
            {
                string gname; double[] pos, rot;
                if (guid.Length > 0 && gateways.TryGetValue(guid, out var g)) { gname = g.Name; pos = g.Pos; rot = g.Rot; }
                else if (rel != null)
                {
                    var r = ShipLadders.Rotate(mg.Rot, rel[0], rel[1], rel[2]);
                    gname = ""; pos = [mg.Pos[0] + r[0], mg.Pos[1] + r[1], mg.Pos[2] + r[2]]; rot = QuatMul(mg.Rot, quat);
                }
                else continue;   // 乗り場のエンティティも、管理からの位置も無い: 位置が決まらないので入れない
                double[]? safe = null;
                if (safeOff != null) { var o = ShipLadders.Rotate(rot, safeOff[0], safeOff[1], safeOff[2]); safe = [pos[0] + o[0], pos[1] + o[1], pos[2] + o[2]]; }
                res.Add(new SocStop(mg.Name, mg.Carriage, gname, pos, safe, stop));
            }
        return res;
    }

    // ship_transit を入れ替える (表が無ければ作る)
    public static void Write(SqliteConnection db, List<Row> rows)
    {
        using var tx = db.BeginTransaction();
        using (var ddl = db.CreateCommand())
        {
            ddl.Transaction = tx;
            ddl.CommandText = Ddl + "DELETE FROM ship_transit;";
            ddl.ExecuteNonQuery();
        }
        using var ins = db.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = "INSERT INTO ship_transit(ship,idx,kind,carriage,container,manager,gateway,x,y,z,sx,sy,sz,line,stop) VALUES(@ship,@idx,@k,@ca,@c,@m,@g,@x,@y,@z,@sx,@sy,@sz,@l,@s)";
        var pShip = ins.Parameters.Add("@ship", SqliteType.Text);
        var pIdx = ins.Parameters.Add("@idx", SqliteType.Integer);
        var pK = ins.Parameters.Add("@k", SqliteType.Text);
        var pCa = ins.Parameters.Add("@ca", SqliteType.Text);
        var pC = ins.Parameters.Add("@c", SqliteType.Text);
        var pM = ins.Parameters.Add("@m", SqliteType.Text);
        var pG = ins.Parameters.Add("@g", SqliteType.Text);
        var pX = ins.Parameters.Add("@x", SqliteType.Real);
        var pY = ins.Parameters.Add("@y", SqliteType.Real);
        var pZ = ins.Parameters.Add("@z", SqliteType.Real);
        var pSx = ins.Parameters.Add("@sx", SqliteType.Real);
        var pSy = ins.Parameters.Add("@sy", SqliteType.Real);
        var pSz = ins.Parameters.Add("@sz", SqliteType.Real);
        var pL = ins.Parameters.Add("@l", SqliteType.Text);
        var pS = ins.Parameters.Add("@s", SqliteType.Text);
        foreach (var r in rows)
        {
            pShip.Value = r.Ship; pIdx.Value = r.Idx; pK.Value = r.Kind; pCa.Value = r.Carriage; pC.Value = r.Container; pM.Value = r.Manager; pG.Value = r.Gateway;
            pX.Value = r.X; pY.Value = r.Y; pZ.Value = r.Z;
            pSx.Value = r.Sx is double sx ? sx : DBNull.Value; pSy.Value = r.Sy is double sy ? sy : DBNull.Value; pSz.Value = r.Sz is double sz ? sz : DBNull.Value;
            pL.Value = r.Line; pS.Value = r.Stop;
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // その船の乗り場の行 (ビューアへ渡す)。DB が無い・表が無い (前の版の DB)・読めないときは空 (ビューアはエレベーターなしで今までどおり)
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
            cmd.CommandText = "SELECT idx,kind,carriage,container,manager,gateway,x,y,z,sx,sy,sz,line,stop FROM ship_transit WHERE ship = @s COLLATE NOCASE ORDER BY idx";
            cmd.Parameters.AddWithValue("@s", shortName);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                res.Add(new Row(shortName, r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetDouble(6), r.GetDouble(7), r.GetDouble(8),
                    r.IsDBNull(9) ? null : r.GetDouble(9), r.IsDBNull(10) ? null : r.GetDouble(10), r.IsDBNull(11) ? null : r.GetDouble(11), r.GetString(12), r.GetString(13)));
        }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException) { res.Clear(); }
        return res;
    }
}
