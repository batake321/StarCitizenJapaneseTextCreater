using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace StarCitizenJapaneseTextCreater;

// ステーションの階層マップの WebView2 (［詳細］の欄の中と、別ウィンドウ StationMapWindow で使う。船の 3D の Ship3DWeb にならう)。
//   利用者データの置き場: 作業ディレクトリの webview2_station (船の webview2_ship3d・詳細の webview2_detail とは分ける)
//   仮想ホスト: scjt-viewer.example → exe の横の wwwroot (ビューア stationmap\viewer.html)。データはファイルから読まず、アプリから PostWebMessageAsJson で渡す
//   ほかへ移る・新しい窓・右クリックのメニューは止める (Ship3DWeb と同じ)。ビューアの失敗 ({type:'error'}) はアプリのログに残す
internal static class StationMapWeb
{
    public const string ViewerHost = Ship3DWeb.ViewerHost;
    public const string ViewerUrl = $"https://{ViewerHost}/stationmap/viewer.html";

    // WebView2 を用意してビューアを開く (一度だけ)。onMessage はビューアからの知らせ (error 以外)。使えなければ例外
    public static async Task InitAsync(WebView2 web, Action<JsonElement> onMessage)
    {
        if (web.CoreWebView2 != null) return;
        var folder = Path.Combine(DetailPanel.WorkDir(), "webview2_station");
        Directory.CreateDirectory(folder);
        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: folder);
        await web.EnsureCoreWebView2Async(env);
        var core = web.CoreWebView2!;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        // 右クリックはビューアの拡大に使う。メニューは出さない
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.NavigationStarting += (_, e) =>
        {
            var uri = e.Uri ?? "";
            if (uri.StartsWith($"https://{ViewerHost}/", StringComparison.OrdinalIgnoreCase)
                || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                || uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;
            e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.WebMessageReceived += (_, e) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "error" && root.TryGetProperty("message", out var m))
                {
                    App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの地図] 表示: {m.GetString()}");
                    return;
                }
                onMessage(root.Clone());
            }
            catch (Exception ex)
            {
                App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの地図] ビューアからの知らせを扱えませんでした: {ex.Message}");
            }
        };
        web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0xee, 0xf0, 0xf2);
        core.SetVirtualHostNameToFolderMapping(ViewerHost, Path.Combine(AppContext.BaseDirectory, "wwwroot"), CoreWebView2HostResourceAccessKind.DenyCors);
        core.Navigate(ViewerUrl);
    }

    public static void Post(WebView2 web, string json)
    {
        try { web.CoreWebView2?.PostWebMessageAsJson(json); }
        catch (Exception ex) { App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの地図] ビューアへ送れませんでした: {ex.Message}"); }
    }

    // ［現在位置］の呼び出し口 (マップタブの［現在位置］と同じ処理 MapPanel.CaptureCurrentAsync。/showlocation を送って App.Config.MapCurrent を入れ替える。
    // 戻り値は取れたかと、マップタブの状態の欄の文)。MainWindow がつなぐ。つなぐ前 (null) は押しても取らない
    // 引数は押したボタンの画面 (アプリのログに出す)
    public static Func<string, Task<(bool Ok, string Status)>>? CaptureHook { get; set; }
}

// ===== 保存した地図 (JSON) を読んだもの =====
// 画面と道順に使う。道順は StationRouteGraph の関数 (ShortestPath・Steps・Describe・NearestNode) で計算する。
// JSON の graph の節点は Id・種類・位置・階だけなので、インスタンスと CryGUID は Id ("<種類の頭文字>:<インスタンス>:<CryGUID>…") から取り、
// 名前は店 (shops の entity) と乗り場 (transit の dest) だけ、位置と CryGUID で引き当てて入れる
// --- 試しのハーネスはここから下 (StationMapModel) を切り出して使う ---
internal sealed class StationMapModel
{
    // DisplayName・Listed はデータが付けた表示の名前と、一覧に出すか (StationRouteGraph.NameSections。無い JSON では "" と true)
    public sealed record SectionInfo(int Index, string Id, string Socpak, string Label, string Name, (double X, double Y, double Z) Center,
        (double X, double Y, double Z) Half, double Yaw, bool Fallback, Dictionary<string, List<List<(double X, double Y)>>> Floors,
        string DisplayName = "", bool Listed = true);
    public sealed record ShopInfo(int Index, string Guid, string Entity, string Socpak, (double X, double Y, double Z) Pos, string Floor, string Section, int Node);
    // Kind: elevator・tram・destination・elevator-noride (位置だけ。乗らない。道順のグラフに辺が無い)。LineInternal は路線の名前が内部のもの (inst:… など。出さない)
    // Place は乗り場を含む区画の名前 (データ側の表示の名前。エレベーターの印の名前は Place、無ければ Dest)
    // NextGateway・NextStop・NextDistrict はトラムの乗り場の次の駅 (CryGUID・行き先の名前 (鍵)・地区の Id。データ側が付ける。無ければ "")
    public sealed record TransitInfo(int Index, string Kind, string Guid, (double X, double Y, double Z) Pos, string Floor, string Dest, string Line, int Node,
        bool LineInternal = false, string Place = "", string NextGateway = "", string NextStop = "", string NextDistrict = "");
    // 道順の結果: Points は線に使う節点の位置と階 (出発から到着まで)、Steps は区切り、Summary は「徒歩 → … → 目的地」の文
    // TramLegs は道順で乗るシャトルの区間 (TransitPaths の番号。全体図で太く見せる)
    public sealed record RouteResult(List<(double X, double Y, double Z, string Floor)> Points, List<StationRouteStep> Steps, double Cost, string Summary,
        List<int>? TramLegs = null);
    // シャトルの道筋 1 区間 (JSON の transitPaths): 路線・向きの名前 (A・B)・乗り場 (from・to の CryGUID)・点
    public sealed record TransitPath(string Line, string Dir, string From, string To, List<(double X, double Y, double Z)> Points);
    // 地区 (JSON の districts。データ側が付ける): 名前・床のまとまり (group)・シャトルの乗り場 (CryGUID)・xy の箱・全体図の印の位置・主要な地区か
    public sealed record District(string Id, string Name, string NameKey, List<string> Groups, List<string> Stations,
        (double MinX, double MinY, double MaxX, double MaxY)? Bounds, (double X, double Y, double Z)? Center, bool Main);

    public sealed record StairInfo((double X, double Y, double Z) Bottom, (double X, double Y, double Z) Top, string From, string To);

    public string Record { get; private set; } = "";
    public string SystemName { get; private set; } = "";
    public StationXf Root { get; private set; } = StationXf.Identity;
    public List<StationFloor> Floors { get; } = new();
    public List<SectionInfo> Sections { get; } = new();
    public List<ShopInfo> Shops { get; } = new();
    public List<TransitInfo> Transit { get; } = new();
    public List<StairInfo> Stairs { get; } = new();
    // シャトル (トラム) の道筋 (JSON の transitPaths。区間ごとの点 (局所)。無い JSON では空)
    public List<TransitPath> TransitPaths { get; } = new();
    // 地区 (JSON の districts。無い JSON では空。空なら全体図は出さず、今までどおり階の地図)
    public List<District> Districts { get; } = new();
    public StationGraph Graph { get; } = new();
    // 斜めに重ねる並び (floors の stackGroup・stackLevel。データが行き先から決めたもの。無い階は入らない)
    public Dictionary<string, (string Group, int Level)> FloorStack { get; } = new(StringComparer.Ordinal);
    // 乗り場の (インスタンス, CryGUID) → 行き先の名前 (Describe に渡す)
    public Dictionary<(int, string), string> StopNames { get; } = new();

    // 前の版の形 (StationMapJson.Format が今より古い、または自転の値の無い JSON)。読まずに「取り込み直しが要る」と出す
    public sealed class OldFormatException(string message) : Exception(message);

    private static string OldText(int fmt) => $"この地図は前の版の形 (形 {fmt}) で取り込んだものです。今の版の形 (形 {StationMapJson.Format}) で取り込み直しが要ります";

    // 読む。前の版の形なら OldFormatException (頭の 64KB だけ見て判断し、大きなファイルを全部は読まない)
    public static StationMapModel Load(string path)
    {
        var fmt = PeekFormat(path);
        if (fmt < StationMapJson.Format) throw new OldFormatException(OldText(fmt));
        return Parse(File.ReadAllBytes(path));
    }

    // JSON の頭の "format" (読めなければ 0)
    public static int PeekFormat(string path)
    {
        var buf = new byte[65536];
        int n;
        using (var fs = File.OpenRead(path)) n = fs.Read(buf, 0, buf.Length);
        var reader = new Utf8JsonReader(buf.AsSpan(0, n), isFinalBlock: false, state: default);
        try
        {
            while (reader.Read())
                if (reader.CurrentDepth == 1 && reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("format"))
                    return reader.Read() && reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var v) ? v : 0;
        }
        catch (JsonException) { }
        return 0;
    }

    public static StationMapModel Parse(byte[] json)
    {
        var m = new StationMapModel();
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (!r.TryGetProperty("app", out var app) || app.GetString() != StationMapJson.App)
            throw new InvalidDataException("ステーションの地図のファイルではありません");
        var fmt = r.TryGetProperty("format", out var fv) && fv.ValueKind == JsonValueKind.Number ? fv.GetInt32() : 0;
        if (fmt < StationMapJson.Format) throw new OldFormatException(OldText(fmt));
        m.Record = Str(r, "record");
        m.SystemName = Str(r, "system");
        if (r.TryGetProperty("root", out var root))
        {
            var p = Vec(root, "pos");
            var q = root.GetProperty("rot");
            m.Root = new StationXf(p.X, p.Y, p.Z, q[0].GetDouble(), q[1].GetDouble(), q[2].GetDouble(), q[3].GetDouble());
            m.RootInfo = ReadRoot(root, m.Record, m.SystemName, out var rule);
            m.Rule = rule;
        }
        if (m.RootInfo == null) throw new OldFormatException("この地図には現在位置を出すのに要る値 (ルートの変換・自転) がありません。取り込み直しが要ります");
        foreach (var f in Arr(r, "floors"))
        {
            var fl = new StationFloor { Id = Str(f, "id") };
            fl.Z = Num(f, "z"); fl.ZMin = Num(f, "zMin"); fl.ZMax = Num(f, "zMax");
            fl.Name = Str(f, "name"); fl.NameSource = Str(f, "nameSource");
            foreach (var l in Arr(f, "labels")) fl.Labels.Add(l.GetString() ?? "");
            // 床のまとまりとその箱 (点の階を xy でも決める。StationFloorBuilder.FloorAtXY)・表示の名前・一覧に出すか・まとまりの中の番号
            fl.Group = Str(f, "group");
            fl.Level = f.TryGetProperty("level", out var lv) && lv.ValueKind == JsonValueKind.Number ? lv.GetInt32() : 0;
            if (f.TryGetProperty("bounds", out var bb) && bb.ValueKind == JsonValueKind.Array && bb.GetArrayLength() == 4)
                fl.Bounds = (bb[0].GetDouble(), bb[1].GetDouble(), bb[2].GetDouble(), bb[3].GetDouble());
            fl.DisplayName = Str(f, "displayName");
            fl.Listed = !(f.TryGetProperty("listed", out var ls) && ls.ValueKind == JsonValueKind.False);
            // エレベーターで止まるだけの階 (病棟・居住区・パッド・ドック・貨物など。階の一覧に出さない)
            fl.ElevatorOnly = f.TryGetProperty("elevatorOnly", out var eo) && eo.ValueKind == JsonValueKind.True;
            // 乗り場からたどれない階 (reachable が false。一覧にも図にも出さない)
            fl.Reachable = !(f.TryGetProperty("reachable", out var rc) && rc.ValueKind == JsonValueKind.False);
            if (Str(f, "stackGroup") is { Length: > 0 } sgr && f.TryGetProperty("stackLevel", out var slv) && slv.ValueKind == JsonValueKind.Number)
                m.FloorStack[fl.Id] = (sgr, slv.GetInt32());
            m.Floors.Add(fl);
        }
        var floorZ = m.Floors.GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First().Z, StringComparer.Ordinal);
        int si = 0;
        foreach (var s in Arr(r, "sections"))
        {
            var floors = new Dictionary<string, List<List<(double X, double Y)>>>(StringComparer.Ordinal);
            if (s.TryGetProperty("floors", out var fs))
                foreach (var fp in fs.EnumerateObject())
                    floors[fp.Name] = fp.Value.EnumerateArray().Select(ring => ring.EnumerateArray().Select(pt => (pt[0].GetDouble(), pt[1].GetDouble())).ToList()).ToList();
            var obb = s.GetProperty("obb");
            m.Sections.Add(new SectionInfo(si++, Str(s, "id"), Str(s, "socpak"), Str(s, "label"), Str(s, "name"), Vec(obb, "center"), Vec(obb, "half"), Num(obb, "yaw"),
                s.TryGetProperty("fallback", out var fb) && fb.ValueKind == JsonValueKind.True, floors,
                Str(s, "displayName"), !(s.TryGetProperty("listed", out var sl) && sl.ValueKind == JsonValueKind.False)));
        }
        foreach (var tp in Arr(r, "transitPaths"))
            m.TransitPaths.Add(new TransitPath(Str(tp, "line"), Str(tp, "dir"), Str(tp, "from"), Str(tp, "to"),
                Arr(tp, "points").Where(q => q.ValueKind == JsonValueKind.Array && q.GetArrayLength() >= 3)
                .Select(q => (q[0].GetDouble(), q[1].GetDouble(), q[2].GetDouble())).ToList()));
        foreach (var d in Arr(r, "districts"))
        {
            (double, double, double, double)? db = d.TryGetProperty("bounds", out var dbv) && dbv.ValueKind == JsonValueKind.Array && dbv.GetArrayLength() == 4
                ? (dbv[0].GetDouble(), dbv[1].GetDouble(), dbv[2].GetDouble(), dbv[3].GetDouble()) : null;
            // center は [x, y] (z が無ければ 0)
            (double, double, double)? dc = d.TryGetProperty("center", out var dcv) && dcv.ValueKind == JsonValueKind.Array && dcv.GetArrayLength() >= 2
                ? (dcv[0].GetDouble(), dcv[1].GetDouble(), dcv.GetArrayLength() >= 3 ? dcv[2].GetDouble() : 0) : null;
            m.Districts.Add(new District(Str(d, "id"), Str(d, "name"), Str(d, "nameKey"),
                Arr(d, "groups").Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList(),
                Arr(d, "stations").Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList(),
                db, dc, d.TryGetProperty("main", out var mv) && mv.ValueKind == JsonValueKind.True));
        }
        foreach (var st in Arr(r, "stairs"))
            m.Stairs.Add(new StairInfo(Vec(st, "bottom"), Vec(st, "top"), Str(st, "from"), Str(st, "to")));
        // 節点 (形 2: 経由点は waypoints に、ほかは nodes に番号の順。StationMapJson.Write の graph の説明)
        var g = r.GetProperty("graph");
        var byKey = new Dictionary<string, int>(StringComparer.Ordinal);
        var objs = Arr(g, "nodes").ToList();
        var runs = Arr(g, "waypoints").Select(w => (At: w.GetProperty("at").GetInt32(), Floor: Str(w, "floor"),
            Ij: w.GetProperty("ij").EnumerateArray().Select(x => x.GetInt32()).ToArray(), Links: Str(w, "links"))).OrderBy(w => w.At).ToList();
        var total = objs.Count + runs.Sum(w => w.Ij.Length / 2);
        var wpAt = new Dictionary<(string F, int Bi, int Bj), int>();
        var wpMask = new List<(int Node, string F, int Bi, int Bj, int Mask)>();
        const double c = StationFloorBuilder.Cell;
        static int Blk(int v) => (int)Math.Floor((double)v / StationRouteGraph.WaypointStep);
        int oi = 0, ri = 0;
        while (m.Graph.Nodes.Count < total)
        {
            var idx = m.Graph.Nodes.Count;
            if (ri < runs.Count && runs[ri].At == idx)
            {
                var run = runs[ri++];
                var z = floorZ.TryGetValue(run.Floor, out var fz) ? fz : 0;
                for (int k = 0; k < run.Ij.Length / 2; k++)
                {
                    int i = run.Ij[2 * k], j = run.Ij[2 * k + 1];
                    var id = $"w:{run.Floor}:{i}:{j}";
                    var ni = m.Graph.Nodes.Count;
                    m.Graph.Nodes.Add(new StationNode(id, "waypoint", ((i + 0.5) * c, (j + 0.5) * c, z), run.Floor, -1, "", -1, ""));
                    m.Graph.ById.TryAdd(id, ni);
                    wpAt[(run.Floor, Blk(i), Blk(j))] = ni;
                    var mask = k < run.Links.Length ? Convert.ToInt32(run.Links[k].ToString(), 16) : 0;
                    if (mask != 0) wpMask.Add((ni, run.Floor, Blk(i), Blk(j), mask));
                }
                continue;
            }
            if (oi >= objs.Count) throw new InvalidDataException("道順のグラフの節点の数が合いません");
            var n = objs[oi++];
            var nid = Str(n, "id");
            var kind = Str(n, "kind");
            var parts = nid.Split(':');
            int inst = -1; string guid = "";
            if (parts.Length >= 3 && int.TryParse(parts[1], out var ii)) { inst = ii; guid = parts[2]; }
            var pos = Vec(n, "pos");
            // 辺は節点の並びの番号で結ぶので、Id が重なっても並びのまま入れる (StationGraph.Add は同じ Id を足さない)
            m.Graph.Nodes.Add(new StationNode(nid, kind, pos, Str(n, "floor"), -1, "", inst, guid));
            m.Graph.ById.TryAdd(nid, idx);
            if (guid.Length > 0) byKey.TryAdd(Key(kind, guid, pos), idx);
        }
        foreach (var e in Arr(g, "edges"))
            m.Graph.Link(new StationEdge(e.GetProperty("a").GetInt32(), e.GetProperty("b").GetInt32(), Num(e, "cost"), Str(e, "kind"), Str(e, "line"), Str(e, "dir"),
                e.TryGetProperty("oneWay", out var ow) && ow.ValueKind == JsonValueKind.True));
        // 経由点どうしの辺 (links の 4 ビット: 区切りの隣 (+1,0)・(0,+1)・(+1,+1)・(+1,-1)。費用は xy の距離)
        var dirs = new[] { (1, 0), (0, 1), (1, 1), (1, -1) };
        foreach (var (a, f, bi, bj, mask) in wpMask)
            for (int d = 0; d < 4; d++)
                if ((mask & (1 << d)) != 0 && wpAt.TryGetValue((f, bi + dirs[d].Item1, bj + dirs[d].Item2), out var b))
                {
                    var pa = m.Graph.Nodes[a].Pos; var pb = m.Graph.Nodes[b].Pos;
                    m.Graph.Link(new StationEdge(a, b, Math.Sqrt((pa.X - pb.X) * (pa.X - pb.X) + (pa.Y - pb.Y) * (pa.Y - pb.Y)), "floor"));
                }
        int shi = 0;
        foreach (var s in Arr(r, "shops"))
        {
            var pos = Vec(s, "pos");
            var guid = Str(s, "guid");
            var node = byKey.TryGetValue(Key("shop", guid, pos), out var ni) ? ni : -1;
            var entity = Str(s, "entity");
            if (node >= 0) m.Graph.Nodes[node] = m.Graph.Nodes[node] with { Name = entity };
            m.Shops.Add(new ShopInfo(shi++, guid, entity, Str(s, "socpak"), pos, Str(s, "floor"), Str(s, "section"), node));
        }
        int ti = 0;
        foreach (var t in Arr(r, "transit"))
        {
            var pos = Vec(t, "pos");
            var guid = Str(t, "guid");
            var kind = Str(t, "kind");
            var dest = Str(t, "dest");
            var node = kind != "destination" && byKey.TryGetValue(Key("gateway", guid, pos), out var ni) ? ni : -1;
            if (node >= 0 && dest.Length > 0)
            {
                m.Graph.Nodes[node] = m.Graph.Nodes[node] with { Name = dest };
                m.StopNames[(m.Graph.Nodes[node].Instance, m.Graph.Nodes[node].Guid)] = dest;
            }
            m.Transit.Add(new TransitInfo(ti++, kind, guid, pos, Str(t, "floor"), dest, Str(t, "line"), node,
                t.TryGetProperty("lineInternal", out var li) && li.ValueKind == JsonValueKind.True, Str(t, "place"),
                Str(t, "nextGateway"), Str(t, "nextStop"), Str(t, "nextDistrict")));
        }
        return m;
    }

    // ビューアへ送る地図 (描くのに要るものだけ。座標は 0.1 m に丸める。道順のグラフは送らない)。
    // floors: 描く階 {id, name, z}、sections: {id, name, fallback, floors: {階: [[x, y, …], …]}} (輪は x, y を並べた 1 次元)、
    // shops: {i, name, pos, floor, section}、transit: 乗り場 {i, kind, name, pos, floor}、stairs: {bottom, top, from, to}、
    // tracks: シャトルの線 [[[x, y], …], …] (道筋の点。無い JSON では tram の辺の両端)。floors には床のまとまり (group・level) も入れる
    public string ViewerMap(IReadOnlyDictionary<string, string> floorNames, IReadOnlyDictionary<int, string> shopNames,
        IReadOnlyDictionary<int, string> transitNames, IReadOnlyDictionary<string, string> sectionNames, IReadOnlyDictionary<string, string>? districtNames = null)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            void R(double v) => w.WriteNumberValue(Math.Round(v, 1));
            w.WriteStartObject();
            w.WriteStartArray("floors");
            foreach (var f in DrawnFloors())
            {
                w.WriteStartObject();
                w.WriteString("id", f.Id); w.WriteString("name", floorNames.TryGetValue(f.Id, out var fn) ? fn : f.Name); w.WritePropertyName("z"); R(f.Z);
                // 階の一覧に出す階か (StationMapPanel の階の一覧と同じ: listed で、エレベーターで止まるだけの階でない)。［上の階］［下の階］はこの階だけを数える
                w.WriteBoolean("listed", f.Listed && !f.ElevatorOnly);
                if (f.Group.Length > 0) { w.WriteString("group", f.Group); w.WriteNumber("level", f.Level); }
                if (FloorStack.TryGetValue(f.Id, out var sk)) { w.WriteString("stackGroup", sk.Group); w.WriteNumber("stackLevel", sk.Level); }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("sections");
            foreach (var s in Sections.Where(s => s.Floors.Count > 0))
            {
                w.WriteStartObject();
                w.WriteString("id", s.Id);
                w.WriteString("name", sectionNames.TryGetValue(s.Id, out var sn) ? sn : s.Name);
                if (s.Fallback) w.WriteBoolean("fallback", true);
                w.WriteStartObject("floors");
                foreach (var (fid, rings) in s.Floors)
                {
                    w.WriteStartArray(fid);
                    foreach (var ring in rings)
                    {
                        w.WriteStartArray();
                        foreach (var p in ring) { R(p.X); R(p.Y); }
                        w.WriteEndArray();
                    }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            void Pt(string name, (double X, double Y, double Z) p) { w.WriteStartArray(name); R(p.X); R(p.Y); R(p.Z); w.WriteEndArray(); }
            var gone = UnreachableFloors();
            w.WriteStartArray("shops");
            foreach (var s in Shops.Where(s => !gone.Contains(s.Floor)))
            {
                w.WriteStartObject();
                w.WriteNumber("i", s.Index); w.WriteString("name", shopNames.TryGetValue(s.Index, out var n) ? n : s.Entity);
                Pt("pos", s.Pos); w.WriteString("floor", s.Floor); w.WriteString("section", s.Section);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("transit");
            foreach (var t in Transit.Where(t => (t.Kind is "elevator" or "tram" or "elevator-noride") && !gone.Contains(t.Floor)))
            {
                w.WriteStartObject();
                w.WriteNumber("i", t.Index); w.WriteString("kind", t.Kind);
                // 路線 (同じ路線の乗り場を平面図で結ぶ)。内部の名前 (lineInternal) でも結ぶのには使う (画面に名前は出さない)
                if (t.Line.Length > 0) w.WriteString("line", t.Line);
                if (transitNames.TryGetValue(t.Index, out var n)) w.WriteString("name", n);
                Pt("pos", t.Pos); w.WriteString("floor", t.Floor);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("stairs");
            foreach (var s in Stairs.Where(s => !gone.Contains(s.From) && !gone.Contains(s.To)))
            {
                w.WriteStartObject();
                Pt("bottom", s.Bottom); Pt("top", s.Top); w.WriteString("from", s.From); w.WriteString("to", s.To);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            // シャトルの線: 道筋 (transitPaths) があれば {dir, from, to, pts: [[x, y], …]} (並びは TransitPaths と同じ。道順の TramLegs の番号で引く)、
            // 無い JSON では道順のグラフの tram の辺の両端
            w.WriteStartArray("tracks");
            if (TransitPaths.Count > 0)
                foreach (var path in TransitPaths)
                {
                    w.WriteStartObject();
                    w.WriteString("dir", path.Dir); w.WriteString("from", path.From); w.WriteString("to", path.To);
                    w.WriteStartArray("pts");
                    foreach (var q in path.Points) { w.WriteStartArray(); R(q.X); R(q.Y); w.WriteEndArray(); }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
            else
            {
                var seen = new HashSet<(int, int)>();
                foreach (var e in Graph.Edges.Where(e => e.Kind == "tram"))
                {
                    if (!seen.Add((Math.Min(e.A, e.B), Math.Max(e.A, e.B)))) continue;
                    var a = Graph.Nodes[e.A]; var b = Graph.Nodes[e.B];
                    w.WriteStartObject();
                    w.WriteString("dir", e.Dir); w.WriteString("from", a.Guid); w.WriteString("to", b.Guid);
                    w.WriteStartArray("pts");
                    w.WriteStartArray(); R(a.Pos.X); R(a.Pos.Y); w.WriteEndArray();
                    w.WriteStartArray(); R(b.Pos.X); R(b.Pos.Y); w.WriteEndArray();
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
            }
            w.WriteEndArray();
            // 地区 (データの districts をそのまま。名前は districtNames があればそれ)
            w.WriteStartArray("districts");
            foreach (var d in Districts)
            {
                w.WriteStartObject();
                w.WriteString("id", d.Id); w.WriteString("name", districtNames != null && districtNames.TryGetValue(d.Id, out var dn) ? dn : d.Name);
                w.WriteBoolean("main", d.Main);
                w.WriteStartArray("groups"); foreach (var g in d.Groups) w.WriteStringValue(g); w.WriteEndArray();
                w.WriteStartArray("stations"); foreach (var g in d.Stations) w.WriteStringValue(g); w.WriteEndArray();
                if (d.Bounds is { } db) { w.WriteStartArray("bounds"); R(db.MinX); R(db.MinY); R(db.MaxX); R(db.MaxY); w.WriteEndArray(); }
                if (d.Center is { } dc) { w.WriteStartArray("center"); R(dc.X); R(dc.Y); R(dc.Z); w.WriteEndArray(); }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string Key(string kind, string guid, (double X, double Y, double Z) p) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{kind}|{guid.ToLowerInvariant()}|{p.X:F2}|{p.Y:F2}|{p.Z:F2}");
    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static double Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
    private static IEnumerable<JsonElement> Arr(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();
    private static (double X, double Y, double Z) Vec(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array || v.GetArrayLength() < 3) return (0, 0, 0);
        return (v[0].GetDouble(), v[1].GetDouble(), v[2].GetDouble());
    }

    // 描く階 (区画の床がある階と、データが一覧に出す (listed) 階。低い順)。乗り場からたどれない階 (Reachable が false) は出さない
    public List<StationFloor> DrawnFloors()
    {
        var used = Sections.SelectMany(s => s.Floors.Keys).ToHashSet(StringComparer.Ordinal);
        return Floors.Where(f => f.Reachable && (used.Contains(f.Id) || (f.Listed && f.DisplayName.Length > 0))).OrderBy(f => f.Z).ToList();
    }

    // 乗り場からたどれない階の Id (この階の店・乗り場・階段は図に出さない)
    public HashSet<string> UnreachableFloors() => Floors.Where(f => !f.Reachable).Select(f => f.Id).ToHashSet(StringComparer.Ordinal);

    // ルート (StationMapJson.Write の root の starPos・starRot・preSpin・postSpin・spin から組み直したもの)。前の版の JSON (それらが無い) なら null
    public StationRoot? RootInfo { get; private set; }
    public StarRotRule Rule { get; private set; } = StationIndex.DefaultRule;
    // 自転する場所か (RootInfo が無ければ分からないので false)
    public bool Spins => RootInfo?.Spins == true;

    private static StationRoot? ReadRoot(JsonElement root, string record, string system, out StarRotRule rule)
    {
        rule = Enum.TryParse<StarRotRule>(Str(root, "rotRule"), out var rr) ? rr : StationIndex.DefaultRule;
        double[]? Nums(string name, int n)
        {
            if (!root.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array || v.GetArrayLength() != n) return null;
            var a = new double[n];
            for (int i = 0; i < n; i++) { if (v[i].ValueKind != JsonValueKind.Number) return null; a[i] = v[i].GetDouble(); }
            return a;
        }
        if (Nums("starPos", 3) is not { } sp || Nums("starRot", 4) is not { } sr || Nums("preSpin", 7) is not { } pre || Nums("postSpin", 7) is not { } post) return null;
        string spinSocpak = "", spinUuid = "";
        double? rotHours = null;
        if (root.TryGetProperty("spin", out var spin) && spin.ValueKind == JsonValueKind.Object)
        {
            spinSocpak = Str(spin, "socpak");
            spinUuid = Str(spin, "uuid");
            if (spin.TryGetProperty("rotHours", out var rh) && rh.ValueKind == JsonValueKind.Number) rotHours = rh.GetDouble();
            if (spinSocpak.Length == 0) return null;
        }
        return new StationRoot
        {
            Place = new StationPlace(record, "", "", system),
            Socpak = Str(root, "socpak"),
            EntityName = "",
            Path = Array.Empty<string>(),
            Depth = 0,
            StarPos = (sp[0], sp[1], sp[2]),
            StarRotRaw = (sr[0], sr[1], sr[2], sr[3]),
            PreSpin = new StationXf(pre[0], pre[1], pre[2], pre[3], pre[4], pre[5], pre[6]),
            PostSpin = new StationXf(post[0], post[1], post[2], post[3], post[4], post[5], post[6]),
            SpinSocpak = spinSocpak,
            SpinUuid = spinUuid,
            SpinRotHours = rotHours,
        };
    }

    // 全体の座標 → 局所の座標 (StationRoot.ToLocal をそのまま使う)。theta は自転の角度 (StationRoot.CorrectedTheta。自転しない場所では使わない)。
    // 前の版の JSON (RootInfo が無い) では null (取り込み直しが要る)
    public (double X, double Y, double Z)? ToLocal((double X, double Y, double Z) global, double theta = 0) =>
        RootInfo?.ToLocal(global, Rule, theta);

    // 局所の点が入る区画の箱 (区画の obb。z 回りの向き yaw (度)) のうちいちばん小さいもの。無ければ null
    public SectionInfo? SectionAt((double X, double Y, double Z) p)
    {
        SectionInfo? best = null;
        double bv = double.MaxValue;
        foreach (var s in Sections)
        {
            double a = -s.Yaw * Math.PI / 180, dx = p.X - s.Center.X, dy = p.Y - s.Center.Y;
            double lx = Math.Cos(a) * dx - Math.Sin(a) * dy, ly = Math.Sin(a) * dx + Math.Cos(a) * dy, lz = p.Z - s.Center.Z;
            if (Math.Abs(lx) > s.Half.X || Math.Abs(ly) > s.Half.Y || Math.Abs(lz) > s.Half.Z) continue;
            var v = s.Half.X * s.Half.Y * s.Half.Z;
            if (v < bv) { bv = v; best = s; }
        }
        return best;
    }

    // 全部の区画の箱を合わせた範囲 (xy・z) に入るか (区画の箱には入らないが、ステーションの中 (ハンガーなど) とみなす範囲)
    public bool InsideBounds((double X, double Y, double Z) p)
    {
        if (Sections.Count == 0) return false;
        double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
        foreach (var s in Sections)
        {
            var r = Math.Sqrt(s.Half.X * s.Half.X + s.Half.Y * s.Half.Y);
            x0 = Math.Min(x0, s.Center.X - r); x1 = Math.Max(x1, s.Center.X + r);
            y0 = Math.Min(y0, s.Center.Y - r); y1 = Math.Max(y1, s.Center.Y + r);
            z0 = Math.Min(z0, s.Center.Z - s.Half.Z); z1 = Math.Max(z1, s.Center.Z + s.Half.Z);
        }
        return p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1 && p.Z >= z0 && p.Z <= z1;
    }

    // 局所の点の階 (StationRouteGraph.NodeFloor。床のセルは保存していないので高さだけで決まる)
    public StationFloor? FloorOf((double X, double Y, double Z) p) => StationRouteGraph.NodeFloor(Floors, null, p);

    // ===== 現在位置の時刻合わせ =====
    // /showlocation の点の高さ − 立っている床の高さ (m) の幅 (この中なら床の上とみなす)。調整役の決め (2026-11-01) で -0.9〜+0.1 m。
    // 確かめた値: Seraphim の Clinic の受付の端末の前の点で -0.89 (データの担当。scratchpad\stage_sera\s3p4b.txt「点−階 -0.89」)、
    // Seraphim の居住区の点で +0.01 (同じ出力の「点−板の上 0.01」)、Orison の Cloudview の前の実測の点で 0.00 (このハーネスで確かめた)
    public const double StandMin = -0.9, StandMax = 0.1;
    // 時刻をずらして探す幅 (秒。取った時刻の前後)・刻み (秒)
    public const double FitWindowSeconds = 1.5, FitStepSeconds = 0.001;

    // 時刻合わせの結果: ずらした秒 (δ)、そのときの局所の点、立っている階、区画 (無ければ null)、候補の数 (選んだものを含む)
    public sealed record StandFit(double Delta, (double X, double Y, double Z) Local, StationFloor Floor, SectionInfo? Section, int Candidates);

    // 階ごとの床の輪 (区画の床の輪をまとめたもの) と、その xy の箱 (最初に使うときに作る)
    private Dictionary<string, List<(List<(double X, double Y)> Ring, double X0, double Y0, double X1, double Y1)>>? _floorRings;

    // 局所の xy が、その階の床の形 (区画の床の輪のどれか) の中にあるか
    public bool OnFloorShape(string floorId, double x, double y)
    {
        if (_floorRings == null)
        {
            var d = new Dictionary<string, List<(List<(double X, double Y)>, double, double, double, double)>>(StringComparer.Ordinal);
            foreach (var s in Sections)
                foreach (var (fid, rings) in s.Floors)
                    foreach (var r in rings)
                    {
                        if (r.Count < 3) continue;
                        double x0 = r.Min(p => p.X), x1 = r.Max(p => p.X), y0 = r.Min(p => p.Y), y1 = r.Max(p => p.Y);
                        if (!d.TryGetValue(fid, out var list)) d[fid] = list = new();
                        list.Add((r, x0, y0, x1, y1));
                    }
            _floorRings = d;
        }
        if (!_floorRings.TryGetValue(floorId, out var rs)) return false;
        foreach (var (r, x0, y0, x1, y1) in rs)
        {
            if (x < x0 || x > x1 || y < y0 || y > y1) continue;
            var inside = false;
            for (int i = 0, j = r.Count - 1; i < r.Count; j = i++)
                if ((r[i].Y > y) != (r[j].Y > y) && x < (r[j].X - r[i].X) * (y - r[i].Y) / (r[j].Y - r[i].Y) + r[i].X) inside = !inside;
            if (inside) return true;
        }
        return false;
    }

    // 取った時刻の前後 FitWindowSeconds 秒の中で時刻をずらし (thetaAt(δ) がその時刻の自転の角度)、点が「どれかの階の床の上」になる時刻を探す。
    // 床の上: 点の高さ − その階の床の高さ が StandMin〜StandMax の中にあり、そのときの xy がその階の床の形 (OnFloorShape) の中にある。
    // δ = 0 から、刻み FitStepSeconds で +・− を交互に外へ広げて調べ、最初に床の上になった δ (絶対値がいちばん小さいもの) を使う
    // (ずらしても局所の z が変わらない場所 (Orison など) でも、床の形の中に入る時刻のうち δ の絶対値がいちばん小さいものになる)。
    // その δ で床の上になる階が 2 つ以上あれば、高さの差が幅の真ん中にいちばん近い階。Candidates はその δ で当たった階の数。
    // 無ければ null (自転しない場所・角度が出せないときも null)
    public StandFit? FitStanding((double X, double Y, double Z) global, Func<double, double?> thetaAt)
    {
        if (RootInfo == null || !Spins) return null;
        OnFloorShape("", 0, 0);   // 床の輪を作る
        var floors = Floors.Where(f => _floorRings!.ContainsKey(f.Id)).ToList();
        if (floors.Count == 0) return null;
        int n = (int)Math.Round(FitWindowSeconds / FitStepSeconds);
        const double mid = (StandMin + StandMax) / 2;
        for (int k = 0; k <= n; k++)
        {
            foreach (var dt in k == 0 ? new[] { 0.0 } : new[] { k * FitStepSeconds, -k * FitStepSeconds })
            {
                if (thetaAt(dt) is not { } th || ToLocal(global, th) is not { } l) continue;
                StationFloor? best = null;
                var hits = 0;
                foreach (var f in floors)
                {
                    var h = l.Z - f.Z;
                    if (h < StandMin || h > StandMax || !OnFloorShape(f.Id, l.X, l.Y)) continue;
                    hits++;
                    if (best == null || Math.Abs(h - mid) < Math.Abs(l.Z - best.Z - mid)) best = f;
                }
                if (best != null) return new StandFit(dt, l, best, SectionAt(l), hits);
            }
        }
        return null;
    }

    // 区画の代表の点 (その階の床のいちばん大きい輪の頂点の平均)。階を指定しなければ、区画の床のいちばん低い階
    public (double X, double Y, double Z)? SectionPoint(SectionInfo s, string? floor = null)
    {
        var fid = floor != null && s.Floors.ContainsKey(floor) ? floor
            : s.Floors.Keys.Select(k => Floors.FirstOrDefault(f => f.Id == k)).Where(f => f != null).OrderBy(f => f!.Z).FirstOrDefault()?.Id;
        // 床の形の無い区画 (データが listed にした、床の取れない区画): 箱の中心の、底の高さの階
        if (fid == null)
            return FloorOf((s.Center.X, s.Center.Y, s.Center.Z - s.Half.Z + 0.5)) is { } bf ? (s.Center.X, s.Center.Y, bf.Z) : null;
        var fl = Floors.First(f => f.Id == fid);
        var ring = s.Floors[fid].OrderByDescending(r => Math.Abs(Area(r))).FirstOrDefault();
        if (ring == null || ring.Count == 0) return null;
        return (ring.Average(q => q.X), ring.Average(q => q.Y), fl.Z);
    }

    private static double Area(List<(double X, double Y)> r)
    {
        double a = 0;
        for (int i = 0; i < r.Count; i++) { var p = r[i]; var q = r[(i + 1) % r.Count]; a += p.X * q.Y - q.X * p.Y; }
        return a / 2;
    }

    // 局所の点にいちばん近い節点 (StationRouteGraph.NearestNode)。kinds があればその種類だけ
    public int NearestNode((double X, double Y, double Z) p, params string[] kinds) => StationRouteGraph.NearestNode(Graph, Floors, p, kinds);

    // 道順 (節点から節点)。道が無ければ null。target は手順の文の最後に付ける目的地の名前。
    // floorNames は階の Id → 表示の名前 (Steps・Describe の「◯階」に使う。無ければ JSON の名前)
    // stopNames は乗り場の (インスタンス, CryGUID) → 表示の行き先の名前 (無ければ JSON の dest のまま)
    public RouteResult? Route(int from, int to, string target, IReadOnlyDictionary<string, string>? floorNames = null,
        IReadOnlyDictionary<(int, string), string>? stopNames = null)
    {
        if (from < 0 || to < 0) return null;
        var pts = new List<(double X, double Y, double Z, string Floor)>();
        void AddPt(int i) { var n = Graph.Nodes[i]; pts.Add((n.Pos.X, n.Pos.Y, n.Pos.Z, n.Floor)); }
        if (from == to)
        {
            AddPt(from);
            return new RouteResult(pts, new List<StationRouteStep>(), 0, target);
        }
        var path = Graph.ShortestPath(from, to);
        if (path == null) return null;
        var floors = floorNames == null ? (IReadOnlyList<StationFloor>)Floors
            : Floors.Select(f =>
            {
                var c = new StationFloor { Id = f.Id, Z = f.Z, ZMin = f.ZMin, ZMax = f.ZMax, NameSource = f.NameSource, FromTransit = f.FromTransit };
                c.Name = floorNames.TryGetValue(f.Id, out var nm) && nm.Length > 0 ? nm : f.Name;
                return c;
            }).ToList();
        var steps = StationRouteGraph.Steps(Graph, floors, path);
        AddPt(from);
        foreach (var (_, t, _) in path) AddPt(t);
        // 乗るシャトルの区間: 道順の tram の辺 (乗り場 → 乗り場) と、乗り場の CryGUID が同じ道筋
        var legs = new List<int>();
        foreach (var (a, b, e) in path.Where(p => p.Edge.Kind == "tram"))
        {
            var i = TransitPaths.FindIndex(tp => string.Equals(tp.From, Graph.Nodes[a].Guid, StringComparison.OrdinalIgnoreCase)
                                              && string.Equals(tp.To, Graph.Nodes[b].Guid, StringComparison.OrdinalIgnoreCase));
            if (i >= 0 && !legs.Contains(i)) legs.Add(i);
        }
        return new RouteResult(pts, steps, Graph.Cost(path), StationRouteGraph.Describe(Graph, steps, target, stopNames ?? StopNames), legs);
    }

    // 区切り 1 つの文 (Describe の 1 区切り分。「 → 」の前まで)
    public string StepText(StationRouteStep step, IReadOnlyDictionary<(int, string), string>? stopNames = null)
    {
        var s = StationRouteGraph.Describe(Graph, new[] { step }, "", stopNames ?? StopNames);
        return s.EndsWith(" → ", StringComparison.Ordinal) ? s[..^3] : s;
    }
}
