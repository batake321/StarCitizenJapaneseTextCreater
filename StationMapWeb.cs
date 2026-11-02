using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace StarCitizenJapaneseTextCreater;

// ステーションの階層マップの WebView2 (［詳細］の欄の中と、別ウィンドウ StationMapWindow で使う。船の 3D の Ship3DWeb にならう)。
//   利用者データの置き場: 作業ディレクトリの webview2_station (［マップ］タブと別ウィンドウで共有。どちらも合成の WebView2。船の webview2_ship3d・詳細の webview2_detail とは分ける。InitAsync の説明)
//   仮想ホスト: scjt-viewer.example → exe の横の wwwroot (ビューア stationmap\viewer.html)。データはファイルから読まず、アプリから PostWebMessageAsJson で渡す
//   ほかへ移る・新しい窓・右クリックのメニューは止める (Ship3DWeb と同じ)。ビューアの失敗 ({type:'error'}) はアプリのログに残す
internal static class StationMapWeb
{
    public const string ViewerHost = Ship3DWeb.ViewerHost;
    public const string ViewerUrl = $"https://{ViewerHost}/stationmap/viewer.html";

    // WebView2 を用意してビューアを開く (一度だけ)。onMessage はビューアからの知らせ (error 以外)。使えなければ例外。
    // web は HWND の WebView2 (別ウィンドウ) か、合成で描く WebView2CompositionControl (［マップ］タブ。重ねて透明度を変えられる) のどちらか (IWebView2)
    // context: ログに書く、今出している場所 (名前と record)。無ければ空
    // onError: ビューアの失敗 ({type:'error'}) を受けたとき (ログに書いた後に呼ぶ。地図の欄に知らせを出すのに使う)
    public static async Task InitAsync(IWebView2 web, Action<JsonElement> onMessage, Func<string>? context = null, Action<JsonElement>? onError = null)
    {
        if (web.CoreWebView2 != null) return;
        // 利用者データの置き場は webview2_station (［マップ］タブと別ウィンドウで共有し、ブラウザーのプロセスを 1 つにする)。
        // (2026-11-02) 公開版 v2.5.1 で、［マップ］タブ (合成の WebView2) が動いている間に、別ウィンドウ (HWND の WebView2) の初期化が 0x8007139F
        // = HRESULT_FROM_WIN32(ERROR_INVALID_STATE) で失敗した。Microsoft の資料 (WebView2 Win32 の CreateCoreWebView2EnvironmentWithOptions) では、同じ置き場の WebView は
        // ブラウザーのプロセスを共有し、今動いている WebView と設定が合わない WebView の作成はこの値で失敗する。別ウィンドウも合成の WebView2 にして種類をそろえた
        // (StationMapWindow.xaml の UseComposition。合成と HWND の違いが「設定が合わない」に当たるかは未確認)
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
                if (type == "error")
                {
                    string Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    // (2026-11-02、利用者の指摘「ログにエラーの情報も出てない」) 「[ステーションの地図] ビューアの失敗: …」として、起きた所 (ファイル:行:列) も書く
                    var at = Str("at");
                    LogScriptError("[ステーションの地図] ビューアの失敗", context?.Invoke() ?? "", Str("where"), (at.Length > 0 ? $"({at}) " : "") + Str("message"), Str("stack"));
                    onError?.Invoke(root.Clone());
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

    // ページのスクリプトのエラー (ステーションの地図の viewer.js・［マップ］タブの星系図の MapScript) をアプリのログに書く。
    // 書くもの: 場所 (place)・処理 (where)・エラーの文・スタックの先頭 ScriptErrorStackLines 行。
    // 同じ文 (prefix・場所・処理・エラーの文が同じ) は ScriptErrorGap に 1 回だけ書く (続けて出てもログがあふれないように。抑えた数は次に書くときに添える)
    private const int ScriptErrorStackLines = 5;
    private static readonly TimeSpan ScriptErrorGap = TimeSpan.FromSeconds(5);
    private static readonly Dictionary<string, (DateTime At, int Skipped)> s_scriptErrors = new(StringComparer.Ordinal);
    public static void LogScriptError(string prefix, string place, string where, string message, string stack)
    {
        var key = $"{prefix}\n{place}\n{where}\n{message}";
        var now = DateTime.UtcNow;
        int skipped;
        lock (s_scriptErrors)
        {
            if (s_scriptErrors.TryGetValue(key, out var last) && now - last.At < ScriptErrorGap)
            {
                s_scriptErrors[key] = (last.At, last.Skipped + 1);
                return;
            }
            skipped = last.Skipped;
            s_scriptErrors[key] = (now, 0);
        }
        var lines = stack.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Take(ScriptErrorStackLines).ToList();
        var sb = new StringBuilder($"{prefix}: 場所 {(place.Length > 0 ? place : "(無し)")}、処理 {(where.Length > 0 ? where : "(不明)")}、{message}");
        if (skipped > 0) sb.Append($" (前に書いてから同じエラーをあと {skipped} 回抑えました)");
        if (lines.Count > 0) sb.Append(" | スタック: ").Append(string.Join(" / ", lines));
        App.AppendStartupLog(App.Config.WorkingDirectory, sb.ToString());
    }

    public static void Post(IWebView2 web, string json)
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
    // TramLegs は道順で乗るシャトルの区間 (TransitPaths の番号)。
    // TramSpans は乗った区間 1 つずつの、道筋の番号と乗る乗り場・降りる乗り場の xy (全体図で、道筋のうち乗る所から降りる所までだけを太く見せる)
    public sealed record RouteResult(List<(double X, double Y, double Z, string Floor)> Points, List<StationRouteStep> Steps, double Cost, string Summary,
        List<int>? TramLegs = null, List<(int Track, double AX, double AY, double BX, double BY)>? TramSpans = null);
    // シャトルの道筋 1 区間 (JSON の transitPaths): 路線・向きの名前 (A・B)・乗り場 (from・to の CryGUID)・点
    public sealed record TransitPath(string Line, string Dir, string From, string To, List<(double X, double Y, double Z)> Points);
    // 地区 (JSON の districts。データ側が付ける): 名前・床のまとまり (group)・シャトルの乗り場 (CryGUID)・xy の箱・全体図の印の位置・主要な地区か
    public sealed record District(string Id, string Name, string NameKey, List<string> Groups, List<string> Stations,
        (double MinX, double MinY, double MaxX, double MaxY)? Bounds, (double X, double Y, double Z)? Center, bool Main);

    // ハンガーの入口 1 つ (JSON の exterior.hangars): 位置 (局所)・GatewaySize・大きさの区分 (S・M・L・XL)
    public sealed record HangarInfo(int Index, (double X, double Y, double Z) Pos, int GatewaySize, string Size);

    // (2026-11-02、利用者の指摘「ハンガー内にいると現在位置がとれない」) ハンガーの中の判定の範囲: 入口 (InstancedHangarGateway) から水平に HangarRadius m 以内で、
    // 高さが入口の HangarDepth m 下から HangarAbove m 上まで (ハンガーの中は地図に無い別の区域で、入口の下にある)。
    // 根拠 (調べの担当 E の 3 点。Checkmate RR_P2_L4、局所の座標): (-454.8, 473.8, 25.9)・(-455.4, 484.6, 28.5)・(-508.3, 572.3, 25.5) は、
    // いちばん近い入口 InstancedHangarGateway_Pyro_LargeTop_RestStop (-511.1, 584.5, 97。L) から水平に 124・114・13 m、71〜72 m 下。
    // 水平は 124 m を含むように 150 m、深さは 72 m の約 2 倍の 150 m、上は入口の高さまで (入口より上はステーションの中の床のことがあるので入れない) にした。
    // Orbituary (RR_P3_LEO) の利用者の点 (2026-11-02 14:44:08・14:44:29) は、入口 (240.4, -717.4, 97。L) から水平に 129 m、26〜28 m 下だった。
    // L 以外の大きさの点はまだ無いので、どの大きさも同じ値 (未確認)
    public const double HangarRadius = 150, HangarDepth = 150, HangarAbove = 0;

    // 局所の点が入るハンガーの範囲 (いくつも当たれば水平にいちばん近い入口)。無ければ null
    public HangarInfo? HangarAt((double X, double Y, double Z) p)
    {
        HangarInfo? best = null; double bd = double.MaxValue;
        foreach (var h in Hangars)
        {
            if (p.Z > h.Pos.Z + HangarAbove || p.Z < h.Pos.Z - HangarDepth) continue;
            var d = Math.Sqrt((p.X - h.Pos.X) * (p.X - h.Pos.X) + (p.Y - h.Pos.Y) * (p.Y - h.Pos.Y));
            if (d <= HangarRadius && d < bd) { bd = d; best = h; }
        }
        return best;
    }

    // ハンガーロビー: 艦隊マネージャーの端末のまとまり (TerminalGroups の kind fleet) のうち、near (ハンガーの入口など) にいちばん近いもの。
    // near が無ければ端末のいちばん多いまとまり。無ければ null
    public TerminalGroup? FleetLobby((double X, double Y, double Z)? near)
    {
        var fleets = TerminalGroups.Where(g => g.Kind == "fleet").ToList();
        if (fleets.Count == 0) return null;
        if (near is { } n) return fleets.MinBy(g => (g.Pos.X - n.X) * (g.Pos.X - n.X) + (g.Pos.Y - n.Y) * (g.Pos.Y - n.Y) + (g.Pos.Z - n.Z) * (g.Pos.Z - n.Z));
        return fleets.OrderByDescending(g => g.Members.Count).First();
    }

    // 点がどれかの階の床の上か (FitStanding と同じ決まり: 点の高さ − 床の高さが StandMin〜StandMax で、xy がその階の床の形の中)
    public bool OnAnyFloor((double X, double Y, double Z) p)
    {
        OnFloorShape("", 0, 0);   // 床の輪を作る
        foreach (var f in Floors)
        {
            var h = p.Z - f.Z;
            if (h < StandMin || h > StandMax || !_floorRings!.ContainsKey(f.Id)) continue;
            if (OnFloorShape(f.Id, p.X, p.Y)) return true;
        }
        return false;
    }

    // (2026-11-02、検証の指摘「中 2・3」) ハンガーの中とみなす点: 床の上でなく (OnAnyFloor)、道順のグラフの節点 (経由点を含む) から MappedNear m 以内に無く
    // (ハンガーの中はステーションの木の外の別の区域 (instanced) で、地図の道から離れている)、ハンガーの範囲 (HangarAt) に入るもの。
    // エレベーター・階段・通路の点 (道の節点の近く) をハンガーにしないため。
    // 親の指示は「巨大でない区画の箱のどれにも入っていない」だったが、Orbituary (RR_P3_LEO) の利用者のハンガーの点 (局所 (358.0, -664.5, 69.0)・(362.8, -661.8, 71.0)) は
    // 区画 s1300 (clinic_lobby_rundown_004_occu_001。箱の半分 377 × 296 × 19.6 m) の箱の中に入っていて、その決まりではハンガーにならなかったので、道の節点からの距離にした
    // (親が 2026-11-02 にこの形でよいと決めた)。値: ハンガーの実例の点から、いちばん近い節点まで Checkmate 72.6〜133.3 m・Orbituary 62.8〜67.9 m (経由点は 159 m 以上) だったので、その半分ほどの 30 m
    public const double MappedNear = 30;
    public HangarInfo? HangarPointAt((double X, double Y, double Z) p) =>
        Hangars.Count == 0 || OnAnyFloor(p) || NearGraph(p, MappedNear) ? null : HangarAt(p);

    // 点から r m 以内に道順のグラフの節点 (経由点を含む) があるか。節点の位置の升目 (NearCell m) を最初に使うときに作る
    private const double NearCell = 10;
    private Dictionary<(long, long, long), List<(double X, double Y, double Z)>>? _nodeGrid;
    public bool NearGraph((double X, double Y, double Z) p, double r)
    {
        if (_nodeGrid == null)
        {
            var g = new Dictionary<(long, long, long), List<(double, double, double)>>();
            foreach (var n in Graph.Nodes)
            {
                var k = ((long)Math.Floor(n.Pos.X / NearCell), (long)Math.Floor(n.Pos.Y / NearCell), (long)Math.Floor(n.Pos.Z / NearCell));
                if (!g.TryGetValue(k, out var l)) g[k] = l = new();
                l.Add(n.Pos);
            }
            _nodeGrid = g;
        }
        long c = (long)Math.Ceiling(r / NearCell);
        long ix = (long)Math.Floor(p.X / NearCell), iy = (long)Math.Floor(p.Y / NearCell), iz = (long)Math.Floor(p.Z / NearCell);
        for (long a = ix - c; a <= ix + c; a++)
            for (long b = iy - c; b <= iy + c; b++)
                for (long d = iz - c; d <= iz + c; d++)
                    if (_nodeGrid.TryGetValue((a, b, d), out var l))
                        foreach (var q in l)
                            if ((q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y) + (q.Z - p.Z) * (q.Z - p.Z) <= r * r) return true;
        // 節点のあいだの長い辺 (エレベーター・階段・徒歩など。シャトル (tram) と経由点どうしの辺は除く) の上も、地図の中とみなす
        // (エレベーターの縦穴・長い階段の途中は、両端の節点から離れていることがあるため。2026-11-02、検証の指摘「中 3」の確かめで Levski・RR_ARC_L4 に当たった)
        if (_edgeSegs == null)
        {
            var segs = new List<((double X, double Y, double Z) A, (double X, double Y, double Z) B)>();
            foreach (var e in Graph.Edges)
            {
                // .rmxml の部屋のつながり (rmxml) は道の形ではない (Checkmate で 3.6 km の辺がハンガーの点のそばを通っていた) ので除く。
                // 長い辺 (FlatSegment m より長い) は、縦に近いもの (高さの差が水平の長さの半分以上。エレベーターの縦穴・斜めの昇降) だけを使う。
                // Levski のエレベーターの辺は水平 331 m・高さ 609 m (長さ 693 m) などで、縦穴の途中の点を地図の中とみなすのに要る。
                // Checkmate には水平に 1.7〜2.4 km・高さの差 4 m の「エレベーター」の辺 (遠くの腕への乗り物) があり、まっすぐの線はハンガーのそば (26 m) を通るが、実際の道筋ではない
                if (e.Kind is "tram" or "floor" or "rmxml" || e.A < 0 || e.B < 0 || e.A >= Graph.Nodes.Count || e.B >= Graph.Nodes.Count) continue;
                var pa = Graph.Nodes[e.A].Pos; var pb = Graph.Nodes[e.B].Pos;
                var len2 = (pa.X - pb.X) * (pa.X - pb.X) + (pa.Y - pb.Y) * (pa.Y - pb.Y) + (pa.Z - pb.Z) * (pa.Z - pb.Z);
                if (len2 <= NearCell * NearCell) continue;   // 短い辺は両端の節点で足りる
                var h2 = (pa.X - pb.X) * (pa.X - pb.X) + (pa.Y - pb.Y) * (pa.Y - pb.Y);
                if (len2 > FlatSegment * FlatSegment && Math.Abs(pa.Z - pb.Z) < Math.Sqrt(h2) / 2) continue;
                segs.Add((pa, pb));
            }
            _edgeSegs = segs;
        }
        foreach (var (sa, sb) in _edgeSegs)
        {
            double ex = sb.X - sa.X, ey = sb.Y - sa.Y, ez = sb.Z - sa.Z, l2 = ex * ex + ey * ey + ez * ez;
            if (Math.Min(sa.X, sb.X) - r > p.X || Math.Max(sa.X, sb.X) + r < p.X || Math.Min(sa.Y, sb.Y) - r > p.Y || Math.Max(sa.Y, sb.Y) + r < p.Y
                || Math.Min(sa.Z, sb.Z) - r > p.Z || Math.Max(sa.Z, sb.Z) + r < p.Z) continue;
            var t = l2 > 0 ? Math.Clamp(((p.X - sa.X) * ex + (p.Y - sa.Y) * ey + (p.Z - sa.Z) * ez) / l2, 0, 1) : 0;
            double qx = sa.X + ex * t - p.X, qy = sa.Y + ey * t - p.Y, qz = sa.Z + ez * t - p.Z;
            if (qx * qx + qy * qy + qz * qz <= r * r) return true;
        }
        return false;
    }
    private List<((double X, double Y, double Z) A, (double X, double Y, double Z) B)>? _edgeSegs;
    // 縦に近くなくても使う辺の長さの上限 (m)。値は仮 (MappedNear 30 m の約 7 倍。部屋の中の徒歩の辺の長さの分布は測っていない)
    private const double FlatSegment = 200;

    // 端末 1 つ (JSON の terminals): Kind は StationTerminalKinds の鍵。Node は道順のグラフの節点の番号 (データ側の NearestNode。無ければ -1)。ShopEntity は店の端末の SCShop の Name
    public sealed record TerminalInfo(int Index, string Kind, string Guid, (double X, double Y, double Z) Pos, string Floor, string Section, int Node, string ShopGuid, string ShopEntity);
    // 端末のまとまり: 同じ種類・同じ階で、xy が TerminalJoin m 以内でつながる端末 (高さの差 TerminalJoinZ m 以内)。Pos・Node・Section はまとまりの真ん中にいちばん近い端末のもの
    public sealed record TerminalGroup(int Index, string Kind, List<int> Members, (double X, double Y, double Z) Pos, string Floor, string Section, int Node);
    // 値: 艦隊マネージャーの端末は 1.25〜3 m おきに並ぶ (RR_HUR_L1 の rs_entry で 1.25・3.0 m。調べの ent_*.tsv)、コモディティの端末も 1.5 m おき。
    // 別々の受付 (精錬所の端末と店の端末は約 10 m 離れる) は分けたいので 6 m にした
    public const double TerminalJoin = 6, TerminalJoinZ = 3;

    // Outline は階段の板を真上から見た外形の輪 (JSON の stairs の outline。板ごとに 1 つの凸包の輪。局所の xy)。板から作れない階段 (乗り場の縁の platform など) と前の版の JSON では null
    public sealed record StairInfo((double X, double Y, double Z) Bottom, (double X, double Y, double Z) Top, string From, string To,
        List<List<(double X, double Y)>>? Outline = null);

    public string Record { get; private set; } = "";
    public string SystemName { get; private set; } = "";
    public StationXf Root { get; private set; } = StationXf.Identity;
    public List<StationFloor> Floors { get; } = new();
    public List<SectionInfo> Sections { get; } = new();
    public List<ShopInfo> Shops { get; } = new();
    public List<TransitInfo> Transit { get; } = new();
    public List<StairInfo> Stairs { get; } = new();
    // 端末 (JSON の terminals。形 18) と、そのまとまり (TerminalGroups。同じ種類で隣り合う端末を 1 つの印・一覧の 1 行にしたもの)
    public List<TerminalInfo> Terminals { get; } = new();
    public List<TerminalGroup> TerminalGroups { get; } = new();
    // ハンガーの入口 (JSON の exterior.hangars。形 18)
    public List<HangarInfo> Hangars { get; } = new();
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
    public class OldFormatException(string message) : Exception(message);
    // 今のアプリより新しい形式の地図 (新しい版のアプリで取り込んだもの。2026-11-02、利用者の指摘「HUR-L5 マップがでない」: 公開版で新しい形式の地図が空白になった)
    public sealed class NewerFormatException(string message) : OldFormatException(message);
    private static string NewerText(int fmt) => $"この地図は新しい版のアプリで取り込んだもの (形 {fmt}) です。今のアプリの形は {StationMapJson.Format} です。アプリを更新するか、取り込み直してください";

    private static string OldText(int fmt) => $"この地図は前の版の形 (形 {fmt}) で取り込んだものです。今の版の形 (形 {StationMapJson.Format}) で取り込み直しが要ります";

    // 読む。前の版の形なら OldFormatException (頭の 64KB だけ見て判断し、大きなファイルを全部は読まない)
    public static StationMapModel Load(string path)
    {
        var fmt = PeekFormat(path);
        if (fmt < StationMapJson.Format) throw new OldFormatException(OldText(fmt));
        if (fmt > StationMapJson.Format) throw new NewerFormatException(NewerText(fmt));
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
        if (fmt > StationMapJson.Format) throw new NewerFormatException(NewerText(fmt));
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
        {
            // 外形 (outline: [[[x, y], …], …]。区画の floors の輪と同じ書き方)。点が 3 つ未満の輪は使わない
            List<List<(double X, double Y)>>? outline = null;
            foreach (var ring in Arr(st, "outline"))
            {
                var pts = ring.ValueKind == JsonValueKind.Array
                    ? ring.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Array && p.GetArrayLength() >= 2).Select(p => (p[0].GetDouble(), p[1].GetDouble())).ToList()
                    : new List<(double, double)>();
                if (pts.Count >= 3) (outline ??= new()).Add(pts);
            }
            m.Stairs.Add(new StairInfo(Vec(st, "bottom"), Vec(st, "top"), Str(st, "from"), Str(st, "to"), outline));
        }
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
        int tmi = 0;
        foreach (var t in Arr(r, "terminals"))
        {
            var node = t.TryGetProperty("node", out var nv) && nv.ValueKind == JsonValueKind.Number ? nv.GetInt32() : -1;
            if (node >= m.Graph.Nodes.Count) node = -1;
            m.Terminals.Add(new TerminalInfo(tmi++, Str(t, "kind"), Str(t, "guid"), Vec(t, "pos"), Str(t, "floor"), Str(t, "section"), node, Str(t, "shop"), Str(t, "shopEntity")));
        }
        m.GroupTerminals();
        if (r.TryGetProperty("exterior", out var exo) && exo.ValueKind == JsonValueKind.Object)
        {
            int hi = 0;
            foreach (var h in Arr(exo, "hangars"))
                m.Hangars.Add(new HangarInfo(hi++, Vec(h, "pos"), h.TryGetProperty("gatewaySize", out var gs) && gs.ValueKind == JsonValueKind.Number ? gs.GetInt32() : 0, Str(h, "size")));
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

    // 端末をまとまり (TerminalGroups) にする。並びは種類 (StationTerminalKinds の順)・階・x・y の順
    private void GroupTerminals()
    {
        TerminalGroups.Clear();
        var left = Terminals.ToList();
        var groups = new List<List<TerminalInfo>>();
        while (left.Count > 0)
        {
            var g = new List<TerminalInfo> { left[0] };
            left.RemoveAt(0);
            for (int i = 0; i < g.Count; i++)
            {
                var a = g[i];
                var near = left.Where(b => b.Kind == a.Kind && b.Floor == a.Floor && Math.Abs(a.Pos.Z - b.Pos.Z) <= TerminalJoinZ
                    && Math.Sqrt((a.Pos.X - b.Pos.X) * (a.Pos.X - b.Pos.X) + (a.Pos.Y - b.Pos.Y) * (a.Pos.Y - b.Pos.Y)) <= TerminalJoin).ToList();
                foreach (var b in near) { g.Add(b); left.Remove(b); }
            }
            groups.Add(g);
        }
        foreach (var g in groups.OrderBy(g => StationTerminalKinds.Order(g[0].Kind)).ThenBy(g => g[0].Floor, StringComparer.Ordinal)
                     .ThenBy(g => g.Average(t => t.Pos.X)).ThenBy(g => g.Average(t => t.Pos.Y)))
        {
            double cx = g.Average(t => t.Pos.X), cy = g.Average(t => t.Pos.Y), cz = g.Average(t => t.Pos.Z);
            var rp = g.MinBy(t => (t.Pos.X - cx) * (t.Pos.X - cx) + (t.Pos.Y - cy) * (t.Pos.Y - cy) + (t.Pos.Z - cz) * (t.Pos.Z - cz))!;
            var node = rp.Node >= 0 ? rp.Node : g.Select(t => t.Node).FirstOrDefault(n => n >= 0, -1);
            TerminalGroups.Add(new TerminalGroup(TerminalGroups.Count, rp.Kind, g.Select(t => t.Index).ToList(), rp.Pos, rp.Floor, rp.Section, node));
        }
    }

    // ビューアへ送る地図 (描くのに要るものだけ。座標は 0.1 m に丸める。道順のグラフは送らない)。
    // floors: 描く階 {id, name, z}、sections: {id, name, fallback, floors: {階: [[x, y, …], …]}} (輪は x, y を並べた 1 次元)、
    // shops: {i, name, pos, floor, section}、transit: 乗り場 {i, kind, name, pos, floor}、stairs: {bottom, top, from, to, outline (あれば)}、
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
                // 区画の名札を地図に出すか (データが一覧に出す区画。2026-11-02、利用者の指示「階も区域も今の文字が見やすいとは思わない」で、選ばなくても名札を出す)
                if (s.Listed) w.WriteBoolean("listed", true);
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
            // 端末 (まとまりごと): {i: まとまりの番号, kind, n: 端末の数, pos, floor}。階の無い端末・たどれない階の端末は出さない (店と同じ)。
            // 種類の言葉と色は termKinds {kind: {label, color}} (StationTerminalKinds)。アイコンはアプリが後から {type:'icons'} で送る
            w.WriteStartArray("terminals");
            foreach (var g in TerminalGroups.Where(g => g.Floor.Length > 0 && !gone.Contains(g.Floor)))
            {
                w.WriteStartObject();
                w.WriteNumber("i", g.Index); w.WriteString("kind", g.Kind); w.WriteNumber("n", g.Members.Count);
                Pt("pos", g.Pos); w.WriteString("floor", g.Floor);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartObject("termKinds");
            foreach (var k in StationTerminalKinds.All)
            {
                w.WriteStartObject(k.Key);
                w.WriteString("label", k.Label); w.WriteString("color", k.Color);
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteStartArray("stairs");
            foreach (var s in Stairs.Where(s => !gone.Contains(s.From) && !gone.Contains(s.To)))
            {
                w.WriteStartObject();
                Pt("bottom", s.Bottom); Pt("top", s.Top); w.WriteString("from", s.From); w.WriteString("to", s.To);
                // 外形があれば outline: [[x, y, x, y, …], …] (区画の床の輪と同じく x, y を並べた 1 次元。ビューアは輪を枠で描き、階段のアイコンは出さない)
                if (s.Outline is { Count: > 0 } ol)
                {
                    w.WriteStartArray("outline");
                    foreach (var ring in ol) { w.WriteStartArray(); foreach (var q in ring) { R(q.X); R(q.Y); } w.WriteEndArray(); }
                    w.WriteEndArray();
                }
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
    // Floor は床の上で合ったときの階 (ハンガーの中で合ったときは null)、Hangar はハンガーの中で合ったときの入口 (床の上なら null)
    public sealed record StandFit(double Delta, (double X, double Y, double Z) Local, StationFloor? Floor, SectionInfo? Section, int Candidates, HangarInfo? Hangar = null);

    // 階ごとの床の輪 (区画の床の輪をまとめたもの) と、その xy の箱 (最初に使うときに作る)
    private Dictionary<string, List<(List<(double X, double Y)> Ring, double X0, double Y0, double X1, double Y1)>>? _floorRings;

    // 局所の xy が、その階の床の形 (区画の床の輪のどれか) の中にあるか。輪の縁から StationFloorBuilder.PaintMargin 以内も中とする
    // (輪は床の板ごとの凸包 (2026-11-02)。前の格子の輪郭は板の縁の PaintMargin の帯まで塗っていたので、同じ幅にする)
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
        const double m = StationFloorBuilder.PaintMargin;
        foreach (var (r, x0, y0, x1, y1) in rs)
        {
            if (x < x0 - m || x > x1 + m || y < y0 - m || y > y1 + m) continue;
            if (StationFloorBuilder.OnRings(new[] { r }, x, y)) return true;
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
        // (2026-11-02、利用者の指摘) 床の上になる時刻が無ければ、ハンガーの中 (HangarAt。地図に床の無い別の区域) になる時刻を同じ順で探す。
        // 床を先に全部探すのは、ハンガーの範囲が広い (水平 150 m) ので、少しずらせば床の上になる点をハンガーにしないため
        // (2026-11-02、検証の指摘「中 2」) ずらす途中で、先に道順のグラフの近く (NearGraph。地図の中の通路・階段・エレベーター) に来たら、ハンガーにはしない
        // (地図の中の床で合わなかった点 (階段の途中・エレベーターの中など) を、遠くまでずらしてハンガーにしないため)
        if (Hangars.Count == 0) return null;
        for (int k = 0; k <= n; k++)
            foreach (var dt in k == 0 ? new[] { 0.0 } : new[] { k * FitStepSeconds, -k * FitStepSeconds })
            {
                if (thetaAt(dt) is not { } th || ToLocal(global, th) is not { } l) continue;
                if (NearGraph(l, MappedNear)) return null;
                if (HangarPointAt(l) is { } h) return new StandFit(dt, l, null, null, 1, h);
            }
        return null;
    }

    // 区画の代表の点 (その階の床の輪の頂点の平均を、輪の面積で重み付けした平均)。階を指定しなければ、区画の床のいちばん低い階
    public (double X, double Y, double Z)? SectionPoint(SectionInfo s, string? floor = null)
    {
        var fid = floor != null && s.Floors.ContainsKey(floor) ? floor
            : s.Floors.Keys.Select(k => Floors.FirstOrDefault(f => f.Id == k)).Where(f => f != null).OrderBy(f => f!.Z).FirstOrDefault()?.Id;
        // 床の形の無い区画 (データが listed にした、床の取れない区画): 箱の中心の、底の高さの階
        if (fid == null)
            return FloorOf((s.Center.X, s.Center.Y, s.Center.Z - s.Half.Z + 0.5)) is { } bf ? (s.Center.X, s.Center.Y, bf.Z) : null;
        var fl = Floors.First(f => f.Id == fid);
        // 輪は床の板ごとの凸包 (2026-11-02 から。前は区画の輪郭)。区画全体の真ん中にするため、輪の頂点の平均を輪の面積で重み付けして平均する
        var rings = s.Floors[fid].Where(r => r.Count > 0).ToList();
        if (rings.Count == 0) return null;
        var wsum = rings.Sum(r => Math.Abs(Area(r)));
        if (wsum <= 0) return (rings.SelectMany(r => r).Average(q => q.X), rings.SelectMany(r => r).Average(q => q.Y), fl.Z);
        return (rings.Sum(r => Math.Abs(Area(r)) * r.Average(q => q.X)) / wsum, rings.Sum(r => Math.Abs(Area(r)) * r.Average(q => q.Y)) / wsum, fl.Z);
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
        // 乗るシャトルの区間: 道順の tram の辺 (乗り場 → 乗り場) と、乗り場の CryGUID が同じ道筋。
        // 同じ CryGUID の道筋が 2 本以上あるときは、辺の両端の乗り場に xy でいちばん近い道筋 (乗り場までの距離の和が最小のもの)。
        // (New Babbage は 8 つの乗り場が同じ CryGUID で、道筋 8 本はどれも乗り場から出て乗り場に戻る環 (約 45 km)。両端から 1 m の道筋と 31 m の道筋 (反対向きの線) がある。
        // 2026-11-01 の取り込みの写しで確かめた)。乗る乗り場・降りる乗り場の xy も渡し、ビューアがその間だけを太くする
        var legs = new List<int>();
        var spans = new List<(int Track, double AX, double AY, double BX, double BY)>();
        foreach (var (a, b, e) in path.Where(p => p.Edge.Kind == "tram"))
        {
            var na = Graph.Nodes[a]; var nb = Graph.Nodes[b];
            var cands = Enumerable.Range(0, TransitPaths.Count).Where(k => string.Equals(TransitPaths[k].From, na.Guid, StringComparison.OrdinalIgnoreCase)
                                                                       && string.Equals(TransitPaths[k].To, nb.Guid, StringComparison.OrdinalIgnoreCase)).ToList();
            if (cands.Count == 0) continue;
            var i = cands.MinBy(k => PolylineDistance(TransitPaths[k].Points, na.Pos.X, na.Pos.Y) + PolylineDistance(TransitPaths[k].Points, nb.Pos.X, nb.Pos.Y));
            if (!legs.Contains(i)) legs.Add(i);
            spans.Add((i, na.Pos.X, na.Pos.Y, nb.Pos.X, nb.Pos.Y));
        }
        return new RouteResult(pts, steps, Graph.Cost(path), StationRouteGraph.Describe(Graph, steps, target, stopNames ?? StopNames), legs, spans);
    }

    // 点 (x, y) から折れ線 (xy) までのいちばん近い距離。点が無ければ無限大
    private static double PolylineDistance(List<(double X, double Y, double Z)> pts, double x, double y)
    {
        if (pts.Count == 0) return double.PositiveInfinity;
        if (pts.Count == 1) return Math.Sqrt((pts[0].X - x) * (pts[0].X - x) + (pts[0].Y - y) * (pts[0].Y - y));
        var best = double.PositiveInfinity;
        for (int k = 1; k < pts.Count; k++)
        {
            var (ax, ay) = (pts[k - 1].X, pts[k - 1].Y); var (dx, dy) = (pts[k].X - ax, pts[k].Y - ay);
            var l2 = dx * dx + dy * dy;
            var t = l2 > 0 ? Math.Clamp(((x - ax) * dx + (y - ay) * dy) / l2, 0, 1) : 0;
            var qx = ax + t * dx - x; var qy = ay + t * dy - y;
            best = Math.Min(best, Math.Sqrt(qx * qx + qy * qy));
        }
        return best;
    }

    // 区切り 1 つの文 (Describe の 1 区切り分。「 → 」の前まで)
    public string StepText(StationRouteStep step, IReadOnlyDictionary<(int, string), string>? stopNames = null)
    {
        var s = StationRouteGraph.Describe(Graph, new[] { step }, "", stopNames ?? StopNames);
        return s.EndsWith(" → ", StringComparison.Ordinal) ? s[..^3] : s;
    }
}

// 端末の種類 (2026-11-02、利用者の指示「ハンガーロビー、クリニック、薬局、再生登録、ショップ、コモディディ、配送センター、鋳造所はマーク付けてほしいし、マークは端末のところがいい」)。
// Key は取り込みの判定 (SocpakContents.ReadTerminals) の鍵。Label は名札・一覧の種類の言葉 (ゲームの訳。アプリの作業フォルダの英語・日本語の global.ini で確かめたもの):
//   fleet「艦隊マネージャー」ShipSelector_Header_Main (この端末の所がハンガーロビー) / clinic「クリニック」(利用者の言葉。仕様書の area_name_clinic。
//   日本語の global.ini には area_name_clinic が無く、Maps_Amenities_Clinic・ui_dest_clinic は「診察所」) / pharmacy「薬局」shop_name_pharmacy /
//   clone「再生登録」(利用者の決定) / shop「ショップ」(仕様書の area_name_Shops。日本語の global.ini には無い) / commodity「コモディティ」shop_ui_commodities /
//   cargo「貨物センター」ui_dest_cargo (Cargo Center) / refinery「精製所」ui_dest_refinery / freight「貨物エレベーター」(英語は InteriorMapLabel_Cargolift「Freight Elevator」。日本語の訳は無かった)。
// Icon はゲームのアイコン (GameIcons。Markers の svg。白一色なので Color の丸の上に載せる)。星図の施設の表 (starmapamenitytypes) にある種類 (Clinic・Refinery・
//   Commodity Trading - Freight Elevator・Commodity Trading - Loading Dock) はそのアイコン、表に無い種類は Markers の中から形の近いものを選んだ
internal static class StationTerminalKinds
{
    public sealed record Info(string Key, string Label, string Icon, string Color);
    public static readonly Info[] All =
    [
        new("fleet", "艦隊マネージャー", "marker_icon_garage.svg", "#1a73e8"),
        new("clinic", "クリニック", "marker_icon_clinic.svg", "#d93025"),
        new("pharmacy", "薬局", "marker_icon_medical.svg", "#e52592"),
        new("clone", "再生登録", "marker_icon_human.svg", "#9334e6"),
        new("shop", "ショップ", "marker_icon_storage.svg", "#188038"),
        new("commodity", "コモディティ", "marker_icon_admin.svg", "#e37400"),
        new("cargo", "貨物センター", "marker_icon_package.svg", "#795548"),
        new("refinery", "精製所", "marker_icon_refinery.svg", "#00897b"),
        new("freight", "貨物エレベーター", "marker_icon_freight_elevator.svg", "#5f6368"),
    ];
    public static int Order(string key) { var i = Array.FindIndex(All, k => k.Key == key); return i < 0 ? All.Length : i; }
    public static Info? Of(string key) => All.FirstOrDefault(k => k.Key == key);
    public static string Label(string key) => Of(key)?.Label ?? key;

    // ゲームのアイコンを data: の URI にしたもの (種類 → URI)。取り出せなければ空 (印は色の丸と頭の 1 字)。取り出せたら覚える
    private static Dictionary<string, string>? _icons;
    // 取り出せなかった Data.p4k の印 (同じ印の間は試し直さない。地図を送るたびに starbreaker の p4k extract を動かさないように。2026-11-02、検証の指摘)
    private static string? _iconsFailedStamp;
    public static async Task<Dictionary<string, string>> IconsAsync()
    {
        if (_icons != null) return _icons;
        var stampKey = Ship3DExports.CurrentStamp()?.Key;
        if (stampKey != null && stampKey == _iconsFailedStamp) return new Dictionary<string, string>(StringComparer.Ordinal);
        var res = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (await GameIcons.EnsureAsync() is { } folder)
                foreach (var k in All)
                {
                    var p = GameIcons.MarkerPath(folder, k.Icon);
                    if (File.Exists(p)) res[k.Key] = "data:image/svg+xml;base64," + Convert.ToBase64String(await File.ReadAllBytesAsync(p));
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの地図] 端末のアイコンを読めませんでした: {ex.Message}");
        }
        if (res.Count > 0) _icons = res;
        else _iconsFailedStamp = stampKey;
        return res;
    }
}
