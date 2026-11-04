using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace StarCitizenJapaneseTextCreater;

// ステーションの地図の新しい方式 (StationView3D) の別ウィンドウ (モードレス。画面はコードで作る)。
// (2026-11-04、利用者の決定「Data.p4k から見る所の区域だけを、開いたときにその場で取り出して描く（前もってデータを作らない）」)
//   開くと: 場所の木 (Data.p4k の索引から) と地図の JSON (取り込み済みのもの。乗り場・階段・店・端末・階の名前) で StationView3D.Session を作り、
//     一時の置き場 (Ship3DExports.WorkFolder の下の view3d_<短い名前>_<GUID>\) に station.json を書いてビューア (wwwroot\stationmap\view3d.html) を開く。
//   ビューアが地点を頼むと ({type:'region', id, c, r}): まわり ±r m の区画だけを取り出して計算し (Session.BuildRegionAsync)、r<id>\ に書いて知らせる ({type:'regionReady'})。
//     取り出している間は {type:'regionProgress', done, total} を送る。ビューアが読み終えたら ({type:'regionLoaded'}) その地点の置き場を消す。窓を閉じたら一時の置き場を全部消す。
//   取り出しの同時数: 1 本は必ず動かし (CPU 使用率が 90% 以上の間は待つ。ゲームのアイコンの取り出しと同じ)、2 本目からは同時数の上限の決まり (Ship3DExports.TryAcquireExtraRun) で足す。
//   WebView2 の用意は船・ステーションの 3D と同じ Ship3DWeb.InitAsync。置き場は Ship3DExports.CacheHost (→ Ship3DExports.Folder) から fetch で読む
// (2026-11-04、利用者の指示「map に出し、別ウィンドウボタンで。」) 画面の中身は StationView3DView (Grid) にして、［マップ］タブの中 (MapPanel) と
// 別ウィンドウ (StationView3DWindow) の両方で同じものを使う
public sealed class StationView3DWindow : Window
{
    public StationView3DView View { get; private set; }
    public StationView3DWindow(string record, string name, Func<string, string, string>? shopLabel = null, Func<string, string, object?>? shopSales = null, string? focusShop = null)
    {
        View = new StationView3DView(record, name, shopLabel, shopSales, focusShop);
        Title = $"ステーションの 3D (新しい方式) - {View.PlaceTitle}";
        Width = 1400; Height = 900;
        Content = View;
        Closed += (_, _) => View.Close3D();
    }

    // (2026-11-04、利用者の指示「別ウィンドウひらいてたらそっちに」) 開いている窓に、別の場所 (か店の行き先) を出し直す: 今の 3D を閉じて新しい 3D に替え、窓を前に出す
    public void ShowPlace(string record, string name, Func<string, string, string>? shopLabel, Func<string, string, object?>? shopSales, string? focusShop)
    {
        var old = View;
        View = new StationView3DView(record, name, shopLabel, shopSales, focusShop);
        Title = $"ステーションの 3D (新しい方式) - {View.PlaceTitle}";
        Content = View;
        old.Close3D();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
}

public sealed class StationView3DView : Grid
{
    // 場所の名前 (窓の題・［マップ］タブの帯に出す)
    public string PlaceTitle => _title;
    public string Record => _record;
    // ［マップ］タブの中に出している 3D か (ビューアに ?tab=1 を渡す。遠ざかり切ったら {type:'leave'} が来て LeaveRequested を起こす)
    public bool InTab { get; init; }
    public event Action? LeaveRequested;
    private readonly string _record, _shortName, _title;
    private readonly WebView2 _web = new();
    private readonly TextBlock _overlay = new() { Foreground = Brushes.White, FontSize = 28, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly CancellationTokenSource _cts = new();
    private CancellationTokenSource? _regionCts;
    private StationView3D.Session? _session;
    // マイハンガーの Session (site → Session。site は "hangar:" + ハンガーの socpak)。ステーションは site ""
    private readonly Dictionary<string, StationView3D.Session> _hangarSessions = new(StringComparer.Ordinal);
    private StationView3D.Loc? _loc;
    private StationView3D.Extractor? _extract;
    private string _tempDir = "";
    private bool _ownInUse;

    // ===== 地点の取り出し結果の保存 (StationView3DCache) と先読み =====
    // (2026-11-04、利用者の指示「先読み、キャッシュ化 HDD 容量設定が空いていればファイル保存など早く動かす仕組みを入れて」)
    //   地点 (site・中心・半径) ごとの結果フォルダを、Data.p4k の印・区画の置き方の一覧の版が同じ間は保存して使い回す。ビューアに渡している間は
    //   lease (保存したもの = 消されないように印を付けたもの / 保存しなかったもの = 一時フォルダで、読み終えたら消す) を持つ。
    //   先読み: ビューアが何も頼まずに PrefetchIdleSeconds 秒たったら、今の場所のまわりの行き先 (station.json の targets) を近い順に、
    //   ビューアが使っている半径で裏で取り出して保存する (1 本ずつ・保存の上限を超えない・空きメモリが足りなければしない・ビューアが頼んだら止める)
    private string _p4kKey = "", _layoutId = "";
    private sealed record Lease(string? CacheKey, string? TempDir);
    private readonly Dictionary<int, Lease> _leases = new();
    private bool _viewerBusy;
    private double[]? _lastCenter;
    private readonly SortedSet<double> _radii = new();
    private List<(string Kind, string Name, double[] Pos)> _targets = new();
    private System.Threading.Timer? _idleTimer;
    private CancellationTokenSource? _prefetchCts;
    private Task? _prefetchTask;
    private int _prefetchBuilt;
    private const int PrefetchIdleSeconds = 20, PrefetchMaxBuilds = 12, PrefetchMaxCenters = 5;
    private const long PrefetchMinFreeMemory = 8L << 30, PrefetchEstimateBytes = 300L << 20;
    private const long PrewarmMinFreeMemory = 16L << 30, PrewarmMinBudget = 4L << 30;
    private const int PrewarmMaxCenters = 2;
    private const double PrefetchSeparation = 30;
    private readonly Func<string, string, string>? _shopLabel;
    private readonly Func<string, string, object?>? _shopSales;

    // shopLabel: 店 (CryGUID, Entity 名) → アプリの地図の店の名前。shopSales: 店 → 販売品の枠の知らせ ({type:'sales', title, shops}。地図の［MAP］の販売品の枠と同じもの)
    // focusShop: 開いたときの行き先にする店 (Entity 名。［詳細］の店の［MAP］)。無ければビューアの決まり
    private readonly string? _focusShop;
    public StationView3DView(string record, string name, Func<string, string, string>? shopLabel = null, Func<string, string, object?>? shopSales = null, string? focusShop = null)
    {
        Interlocked.Increment(ref s_open);
        _record = record; _shopLabel = shopLabel; _shopSales = shopSales; _focusShop = focusShop;
        _shortName = StationPlaces.ShortName(record);
        _title = string.IsNullOrWhiteSpace(name) ? _shortName : name;
        Background = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11));
        Children.Add(_web);
        Children.Add(_overlay);
        // 初めて画面に出たときに始める (［マップ］タブで隠してから出し直しても、始めるのは 1 回)
        Loaded += async (_, _) => { if (_started || _closed) return; _started = true; await StartAsync(); };
    }
    private bool _started;
    private bool _closed;

    // 開いているステーションの 3D の数 (作ってから Close3D するまで)。0 になったら部品の形の置き場も捨てる
    private static int s_open;

    // 閉じる (窓を閉じた・［マップ］タブでほかの場所に替えた・星系図に戻った): 取り出しを止め、WebView2 を捨て、一時の置き場を消す。
    // (2026-11-04、利用者の指示「３D 表示後のメモリ後始末」) 区画の結果・使い回しの置き場 (Session) を手放し、3D がどこにも開いていなければ
    // 部品の形の置き場 (SocpakExporter) も捨てて GC で返す
    public void Close3D()
    {
        if (_closed) return;
        _closed = true;
        _cts.Cancel();
        _regionCts?.Cancel();
        _idleTimer?.Dispose(); _idleTimer = null;
        _prefetchCts?.Cancel();
        ReleaseLeases();
        try { _web.Dispose(); } catch { }
        DeleteTemp();
        _session = null;
        _hangarSessions.Clear();
        _extract = null;
        _loc = null;
        if (Interlocked.Decrement(ref s_open) <= 0)
        {
            s_open = 0;
            SocpakExporter.ReleaseShared();
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect();
    }

    private void Overlay(string text)
    {
        _overlay.Text = text;
        _overlay.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task StartAsync()
    {
        if (Ship3DExports.CurrentStamp() is not { } stamp) { _web.Visibility = Visibility.Collapsed; Overlay("ゲームの Data.p4k が見つかりません。設定タブのゲームのフォルダとチャンネルを確かめてください。"); return; }
        Overlay("準備中…");
        var ct = _cts.Token;
        try
        {
            _tempDir = Path.Combine(Ship3DExports.WorkFolder, $"view3d_{_shortName}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
            var record = _record;
            _session = await Task.Run(() =>
            {
                // 区画の置き方の一覧と場所の中身 (DB。LayoutFor)。2D の地図の JSON は使わない (2026-11-04、利用者の決定「名前を 2d からとるのをやめるだけの話」)
                var layout = LayoutFor(record, stamp, ct);
                _stationLayout = layout;
                var ini = Path.Combine(Path.GetDirectoryName(stamp.Path) ?? "", "data", "Localization", "japanese_(japan)", "global.ini");
                var loc = new StationView3D.Loc(ini, Path.Combine(DetailPanel.WorkDir(), "translations.db"));
                _loc = loc;
                var s = new StationView3D.Session(layout, "", loc, terminalLabel: StationTerminalKinds.Label, shopLabel: _shopLabel);
                var stationJson = s.StationJson();
                File.WriteAllBytes(Path.Combine(_tempDir, "station.json"), stationJson);
                // 保存の鍵 (Data.p4k の印と区画の置き方の一覧の版・作った時刻)・先読みの行き先
                _p4kKey = stamp.Key;
                _layoutId = (StationView3D.LayoutDbMeta(StationView3DLayout.DbPath, "format") ?? "") + "|" + (StationView3D.LayoutDbMeta(StationView3DLayout.DbPath, "built_utc") ?? "");
                _targets = TargetsOf(stationJson);
                var cache = StationView3DCache.Current;
                cache.SweepStale(_p4kKey, _layoutId);
                return s;
            }, ct);
            _extract = StationView3D.OwnExtractor(stamp.Path,
                m => App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D (新しい方式)] 「{_title}」 {m}"));
            await Ship3DWeb.InitAsync(_web);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _web.Visibility = Visibility.Collapsed;
            Overlay($"3D (新しい方式) を開けませんでした: {ex.Message}");
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D (新しい方式)] 「{_title}」を開けませんでした: {ex}");
            return;
        }
        var core = _web.CoreWebView2!;
        core.WebMessageReceived += OnMessage;
        var folder = Ship3DExports.Folder;
        try { core.ClearVirtualHostNameToFolderMapping(Ship3DExports.CacheHost); } catch { }
        core.SetVirtualHostNameToFolderMapping(Ship3DExports.CacheHost, folder, CoreWebView2HostResourceAccessKind.Allow);
        Overlay("");
        core.Navigate($"https://{Ship3DWeb.ViewerHost}/stationmap/view3d.html?data={Uri.EscapeDataString(UrlOf(_tempDir))}&title={Uri.EscapeDataString(_title)}&app=1" + (InTab ? "&tab=1" : "") + (_focusShop is { Length: > 0 } fs ? $"&goto={Uri.EscapeDataString(fs)}" : ""));
    }

    // 置き場 (Ship3DExports.Folder) の下のフォルダの URL (最後に /)
    private static string UrlOf(string dir)
    {
        var rel = Path.GetRelativePath(Ship3DExports.Folder, dir).Replace('\\', '/');
        return $"https://{Ship3DExports.CacheHost}/{string.Join("/", rel.Split('/').Select(Uri.EscapeDataString))}/";
    }

    private void Post(object o)
    {
        try { _web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(o)); }
        catch (Exception ex) { App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D (新しい方式)] ビューアへ送れませんでした: {ex.Message}"); }
    }

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var r = doc.RootElement;
            var type = r.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "regionLoaded" && r.TryGetProperty("id", out var lid))
            {
                // ビューアが読み終えた地点の置き場を手放す (保存したものは印を外すだけで残す。保存しなかったものは消す)
                var lidv = lid.GetInt32();
                var d = Path.Combine(_tempDir, $"r{lidv}");
                try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                ReleaseLease(lidv);
                _viewerBusy = false;
                ScheduleIdle();
                return;
            }
            if (type == "wcacheUsed" && r.TryGetProperty("bytes", out var wb) && wb.TryGetInt64(out var wbytes)) { StationView3DCache.WorkerBytes = wbytes; return; }
            if (type == "prewarmDone") { _prewarmTcs?.TrySetResult(true); return; }
            if (type == "sales")
            {
                // 店の端末を押したとき (2026-11-04、利用者の指示「押したら売ってるリストと値段を出してほしい」): 端末の店 (地図の JSON の端末の shop・shopEntity) の販売品
                var g = r.TryGetProperty("guid", out var gv) ? gv.GetString() ?? "" : "";
                var en = r.TryGetProperty("entity", out var ev) ? ev.GetString() ?? "" : "";
                var msg = _shopSales?.Invoke(g, en);
                Post(msg ?? new { type = "sales", title = "販売品", shops = new List<object> { new { name = en, note = "販売データが紐づいていません。" } } });
                return;
            }
            if (type == "leave")
            {
                if (InTab) LeaveRequested?.Invoke();
                return;
            }
            if (type == "here")
            {
                await HereAsync();
                return;
            }
            if (type == "hangar")
            {
                await OpenHangarAsync(r.TryGetProperty("size", out var zv) ? zv.GetString() ?? "" : "");
                return;
            }
            if (type == "walkCheck")
            {
                await WalkCheckAsync(r);
                return;
            }
            if (type != "region" || _session == null || _extract == null) return;
            var site = r.TryGetProperty("site", out var sv) ? sv.GetString() ?? "" : "";
            var siteSession = site.Length == 0 ? _session : _hangarSessions.GetValueOrDefault(site);
            if (siteSession == null) { Post(new { type = "regionError", id = r.GetProperty("id").GetInt32(), message = "マイハンガーの用意が終わっていません" }); return; }
            var id = r.GetProperty("id").GetInt32();
            var c = r.GetProperty("c").EnumerateArray().Select(x => x.GetDouble()).ToArray();
            var rad = r.GetProperty("r").GetDouble();
            _regionCts?.Cancel();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _regionCts = cts;
            var dir = Path.Combine(_tempDir, $"r{id}");
            var progress = new Progress<(int Done, int Total)>(p => Post(new { type = "regionProgress", id, done = p.Done, total = p.Total }));
            // ビューアが頼んだので、先読みは止める。前の地点の手放していない置き場 (読み込みに失敗して regionLoaded が来なかったもの) も手放す
            _viewerBusy = true; _lastCenter = c; _radii.Add(rad); _walkCenters[site] = c;
            _idleTimer?.Dispose(); _idleTimer = null;
            await StopPrefetchAsync();
            ReleaseLeases();
            try
            {
                var session = siteSession; var extract = _extract;
                var cache = StationView3DCache.Current;
                var siteKey = site.Length == 0 ? _record : site;
                var key = cache.Enabled ? StationView3DCache.KeyOf(siteKey, _p4kKey, _layoutId, c, rad) : "";
                // 保存したものがあれば、それをそのままビューアに渡す (取り出し・計算をしない)
                if (key.Length > 0 && cache.TryGet(key, siteKey, _p4kKey, _layoutId, c, rad) is { } hit)
                {
                    _leases[id] = new Lease(key, null);
                    Post(WithWorkerCache(new { type = "regionReady", id, @base = UrlOf(hit.Dir), extractSeconds = 0.0, computeSeconds = 0.0, seconds = 0.0, socpaks = hit.Socpaks, missing = hit.Missing, cached = true }, cache, key));
                    return;
                }
                // 保存できそうなら、保存先の書きかけの場所に作る (できたら名前を替えて保存にする。ビューアはそのまま読む)
                var stage = key.Length > 0 && cache.CanStore(PrefetchEstimateBytes) ? cache.BeginStage(key) : null;
                var st = await Task.Run(() => session.BuildRegionAsync(c, rad, stage ?? dir, extract, AcquireAsync, progress, cts.Token), cts.Token);
                var served = stage ?? dir; var stored = false;
                if (stage != null)
                {
                    if (cache.Commit(stage, key, siteKey, _p4kKey, _layoutId, c, rad, st.Socpaks, st.Missing) is { } saved) { served = saved.Dir; _leases[id] = new Lease(key, null); stored = true; }
                    else _leases[id] = new Lease(null, stage);   // 保存できなかった (上限・空き): 読み終えたら消す
                }
                Post(WithWorkerCache(new { type = "regionReady", id, @base = UrlOf(served), extractSeconds = st.ExportSeconds, computeSeconds = st.ComputeSeconds, seconds = st.BuildSeconds, socpaks = st.Socpaks, missing = st.Missing }, cache, stored ? key : ""));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D (新しい方式)] 「{_title}」の地点を作れませんでした: {ex}");
                Post(new { type = "regionError", id, message = ex.Message });
                _viewerBusy = false; ScheduleIdle();
            }
        }
        catch (Exception ex)
        {
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D (新しい方式)] ビューアからの知らせを扱えませんでした: {ex.Message}");
        }
    }

    // ===== 歩くモードの裏の補い (2026-11-05、利用者の指示「歩くモードで近く 5 m 以内の部品を裏で先に細かい形で調べる 見つけたら DB に入れる」
    // 「そのエリアに荒いので部品がない場合、細かいのでチェック」「床でなく物体で」) =====
    // ビューアが 1 秒ごとに知らせる、粗い形に物体の無い升 ({type:'walkCheck', pos, zTol, cells, site}。場所の座標) を、裏 (Task.Run) で細かい形で調べる (Session.WalkCheck)。
    // 見つかった三角形は view3d_fill_walk.db に入れ (StationFill.WriteWalk)、ビューアに {type:'walkFill', center, items:[{inst, name, tris}]} で返す
    // (tris は地点の中心 center からの相対。center は、その site で最後に BuildRegionAsync に渡した中心)。同時に 1 本だけ (順に待つ)。失敗はログに残して続ける
    private readonly SemaphoreSlim _walkGate = new(1, 1);
    private readonly Dictionary<string, double[]> _walkCenters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StationView3D.Session.WalkFillState> _walkStates = new(StringComparer.Ordinal);
    private async Task WalkCheckAsync(JsonElement r)
    {
        if (_closed || Ship3DExports.CurrentStamp() is not { } stamp) return;
        var site = r.TryGetProperty("site", out var sv) ? sv.GetString() ?? "" : "";
        var session = site.Length == 0 ? _session : _hangarSessions.GetValueOrDefault(site);
        if (session == null || !_walkCenters.TryGetValue(site, out var center)) return;
        if (!r.TryGetProperty("pos", out var pv) || pv.ValueKind != JsonValueKind.Array || pv.GetArrayLength() != 3 || !r.TryGetProperty("cells", out var cv) || cv.ValueKind != JsonValueKind.Array) return;
        var pos = pv.EnumerateArray().Select(x => x.GetDouble()).ToArray();
        var zTol = r.TryGetProperty("zTol", out var zv) && zv.ValueKind == JsonValueKind.Number ? zv.GetDouble() : 0.5;
        var cells = cv.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.Array && c.GetArrayLength() >= 2).Select(c => c.EnumerateArray().Select(x => x.GetDouble()).ToArray()).ToList();
        if (cells.Count == 0) return;
        if (!_walkStates.TryGetValue(site, out var state)) _walkStates[site] = state = new StationView3D.Session.WalkFillState();
        var ct = _cts.Token; var title = _title; var p4kPath = stamp.Path;
        void Log(string m) => App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D (新しい方式)] 「{title}」の歩くモードの補い: {m}");
        try { await _walkGate.WaitAsync(ct); } catch (OperationCanceledException) { return; }
        try
        {
            var items = await Task.Run(() =>
            {
                var found = session.WalkCheck(p4kPath, pos, zTol, cells, state, Log, ct);
                foreach (var it in found)
                {
                    ct.ThrowIfCancellationRequested();
                    try { StationFill.WriteWalk(StationFill.WalkDbPath, it.Kind, it.Sig, it.LocalTris); }
                    catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException or InvalidDataException) { Log($"DB に入れられませんでした ({it.Kind}): {ex.Message}"); }
                }
                return found;
            }, ct);
            if (_closed || items.Count == 0) return;
            Post(new
            {
                type = "walkFill",
                center,
                items = items.Select(it =>
                {
                    var t = new float[it.WorldTris.Length];
                    for (int i = 0; i < t.Length; i++) t[i] = (float)(it.WorldTris[i] - center[i % 3]);
                    return new { inst = it.Inst, name = it.Name, tris = t };
                }).ToList(),
            });
            Log($"細かい形で {items.Count} 部品・{items.Sum(i => i.WorldTris.Length / 9)} 三角形を補いました (升 {cells.Count}・高さ {pos[2]:F2} ±{zTol:F2} m): " + string.Join(", ", items.Take(8).Select(i => $"{i.Name} (inst {i.Inst})")) + (items.Count > 8 ? " ほか" : ""));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log($"失敗しました: {ex}"); }
        finally { _walkGate.Release(); }
    }

    // ===== 保存したものの手放し・先読み (上の説明) =====
    private void ReleaseLease(int id)
    {
        if (!_leases.Remove(id, out var l)) return;
        if (l.CacheKey != null) StationView3DCache.Current.Release(l.CacheKey);
        else if (l.TempDir != null) StationView3DCache.TryDelete(l.TempDir);
    }
    private void ReleaseLeases() { foreach (var id in _leases.Keys.ToList()) ReleaseLease(id); }

    // station.json の targets のうち、位置のあるもの (種類・名前・位置)
    private static List<(string Kind, string Name, double[] Pos)> TargetsOf(byte[] stationJson)
    {
        var res = new List<(string, string, double[])>();
        try
        {
            using var doc = JsonDocument.Parse(stationJson);
            if (!doc.RootElement.TryGetProperty("targets", out var ts) || ts.ValueKind != JsonValueKind.Array) return res;
            foreach (var t in ts.EnumerateArray())
            {
                if (!t.TryGetProperty("pos", out var p) || p.ValueKind != JsonValueKind.Array || p.GetArrayLength() != 3) continue;
                var pos = p.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                res.Add((t.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "", t.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "", pos));
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { }
        return res;
    }

    // ビューアが何も頼まずに PrefetchIdleSeconds 秒たったら、先読みを始める
    private void ScheduleIdle()
    {
        _idleTimer?.Dispose(); _idleTimer = null;
        if (_closed || _viewerBusy || _prefetchBuilt >= PrefetchMaxBuilds || !StationView3DCache.Current.Enabled) return;
        _idleTimer = new System.Threading.Timer(_ => Dispatcher.BeginInvoke(StartPrefetch), null, TimeSpan.FromSeconds(PrefetchIdleSeconds), Timeout.InfiniteTimeSpan);
    }

    // 地点の知らせ (regionReady) に、解析の結果の保存 (ビューアの WC) の鍵・使ってよい大きさ・ドライブの空きを足す。
    // 鍵は地点の保存の鍵 (地点が保存されたときだけ。保存されていない地点は解析の結果も保存しない)。大きさ 0 = 保存しない設定 (ビューアは今ある保存を消す)
    private static object WithWorkerCache(object msg, StationView3DCache cache, string key)
    {
        var budget = cache.WorkerBudget();
        var free = StationView3DCache.WorkerFreeBytes();
        var o = System.Text.Json.JsonSerializer.SerializeToNode(msg)!.AsObject();
        o["wkey"] = key.Length > 0 && budget > 0 ? key : null;
        o["wbudget"] = budget;
        o["wfree"] = free;
        return o;
    }

    private TaskCompletionSource<bool>? _prewarmTcs;

    private async Task StopPrefetchAsync()
    {
        if (_prewarmTcs != null) { _prewarmTcs.TrySetCanceled(); Post(new { type = "prewarmCancel" }); }
        _prefetchCts?.Cancel();
        var t = _prefetchTask;
        if (t != null) { try { await t; } catch (OperationCanceledException) { } catch (Exception) { } }
        _prefetchTask = null;
    }

    private void StartPrefetch()
    {
        if (_closed || _viewerBusy || _prefetchTask != null || _session == null || _extract == null || _lastCenter == null || _radii.Count == 0) return;
        if (_prefetchBuilt >= PrefetchMaxBuilds || !StationView3DCache.Current.Enabled) return;
        var center = _lastCenter; var radii = _radii.ToList(); var session = _session; var extract = _extract;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _prefetchCts = cts;
        // ビューアが頼んだ site "" (ステーション本体) の行き先だけ。ハンガーの中は先読みしない
        _prefetchTask = Task.Run(() => PrefetchAsync(session, extract, center, radii, cts.Token));
    }

    // 解析の結果 (ビューアの WC) の先読み: ビューアに別の糸で解析して保存だけしてもらう。
    // 解析は 1 地点で数 GB のメモリを使う (Seraphim で 5.5 GB ほど) ので、空き物理メモリが PrewarmMinFreeMemory 以上のとき、
    // 解析の結果に使ってよい大きさが PrewarmMinBudget 以上で、ドライブの空きがその分あるときだけ頼む。ビューアが地点を頼んだら止める
    private async Task PrewarmAsync(StationView3DCache cache, string dir, string key, double[] c, double r, CancellationToken ct)
    {
        if (!cache.Enabled || _closed) return;
        var budget = cache.WorkerBudget();
        if (budget < PrewarmMinBudget) return;
        var free = StationView3DCache.WorkerFreeBytes();
        if (free is long f && f - PrewarmMinBudget < StationView3DCache.FreeReserveBytes) return;
        if (Ship3DExports.CpuHighNow) return;
        if (Ship3DExports.AvailablePhysicalMemory() is not long mem || mem < PrewarmMinFreeMemory) return;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.InvokeAsync(() =>
        {
            if (_closed || _viewerBusy) { tcs.TrySetResult(false); return; }
            _prewarmTcs = tcs;
            Post(new { type = "prewarm", pid = 1, @base = UrlOf(dir), key, c, r, budget, free });
        });
        try
        {
            using var reg = ct.Register(() => tcs.TrySetCanceled());
            await tcs.Task.WaitAsync(TimeSpan.FromMinutes(20), ct);
        }
        finally { await Dispatcher.InvokeAsync(() => { if (ReferenceEquals(_prewarmTcs, tcs)) _prewarmTcs = null; }); }
    }

    // 近い行き先から順に、ビューアが使った半径 (小さい方から) で作って保存する。1 本ずつ。次の場合は止める:
    //   ビューアが頼んだ (取り消し)・CPU 使用率が 90% 以上・空きメモリが PrefetchMinFreeMemory 未満・保存の上限を超えそう (古いものを消してまで先読みしない)・空きが足りない
    private async Task PrefetchAsync(StationView3D.Session session, StationView3D.Extractor extract, double[] from, List<double> radii, CancellationToken ct)
    {
        var cache = StationView3DCache.Current;
        try
        {
            // 行き先: 今の中心から近い順。同じ所 (PrefetchSeparation m 以内) は 1 つにまとめる。今の中心のまわりは、ビューアが読んだので除く
            var chosen = new List<double[]> { from };
            var plan = new List<double[]>();
            double Dist(double[] a, double[] b) => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));
            foreach (var t in _targets.OrderBy(t => Dist(t.Pos, from)))
            {
                if (chosen.Any(c => Dist(c, t.Pos) <= PrefetchSeparation)) continue;
                chosen.Add(t.Pos); plan.Add(t.Pos);
                if (plan.Count >= PrefetchMaxCenters) break;
            }
            int prewarmed = 0;
            foreach (var r in radii)
            {
                foreach (var c in plan)
                {
                    ct.ThrowIfCancellationRequested();
                    if (_prefetchBuilt >= PrefetchMaxBuilds || !cache.Enabled) return;
                    // CPU が高い間は待つ (ゲームを動かしているとき)。長く続くなら諦める
                    for (int w = 0; Ship3DExports.CpuHighNow; w++) { if (w >= 12) return; await Task.Delay(5000, ct); }
                    if (Ship3DExports.AvailablePhysicalMemory() is long free && free < PrefetchMinFreeMemory) return;
                    var key = StationView3DCache.KeyOf(_record, _p4kKey, _layoutId, c, r);
                    var hit = cache.TryGet(key, _record, _p4kKey, _layoutId, c, r);
                    if (hit == null)
                    {
                        // 古いものを消してまで先読みしない
                        if (!cache.CanStore(PrefetchEstimateBytes) || cache.UsedBytes() + PrefetchEstimateBytes > StationView3DCache.MaxBytesSetting) return;
                        var stage = cache.BeginStage(key);
                        var st = await session.BuildRegionAsync(c, r, stage, extract, AcquireAsync, null, ct);
                        ct.ThrowIfCancellationRequested();
                        hit = cache.Commit(stage, key, _record, _p4kKey, _layoutId, c, r, st.Socpaks, st.Missing);
                        if (hit == null) StationView3DCache.TryDelete(stage);
                        Interlocked.Increment(ref _prefetchBuilt);
                        App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D (新しい方式)] 「{_title}」を先読みして保存しました: 中心 {string.Join(",", c.Select(v => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)))} ±{r} m ({st.BuildSeconds:F1} 秒)");
                    }
                    if (hit == null) continue;
                    try
                    {
                        // 解析の結果 (worker) の先読み: いちばん小さい半径の、近い PrewarmMaxCenters か所だけ。メモリに余裕があるときだけ (PrewarmAsync)
                        if (r == radii[0] && prewarmed < PrewarmMaxCenters) { prewarmed++; await PrewarmAsync(cache, hit.Dir, key, c, r, ct); }
                    }
                    finally { cache.Release(key); }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D (新しい方式)] 「{_title}」の先読みを止めました: {ex.Message}");
        }
        finally
        {
            // 取り消しで書きかけが残っていれば BuildRegionAsync が消している。止めた後、ビューアが静かなままなら次の機会にまた始める
            try { await Dispatcher.InvokeAsync(() => { _prefetchTask = null; }); } catch (Exception) { }   // 窓を閉じた後は Dispatcher が止まっている
        }
    }

    // マイハンガー (2026-11-04、利用者の指示「マイハンガーの S M L XL がない」): 旧アプリ (StationMapPanel.ShowHangarAsync) と同じく、
    // StationHangarCatalog.Resolve (場所の既定のハンガー → 入口の class) でハンガーを選び、ハンガーの地図を用意 (StationMapExports.EnsureHangarAsync。無ければ取り込む) して、
    // その区画の置き方の一覧 (無ければゲームデータの索引と場所の木から作る) で Session を作る。ビューアには {type:'hangarReady', site, data, title, how} を返す
    private async Task OpenHangarAsync(string size)
    {
        var title = $"マイハンガー（{size}）";
        try
        {
            if (_loc == null) throw new InvalidOperationException("まだ準備中です");
            if (Ship3DExports.CurrentStamp() is not { } stamp) throw new InvalidOperationException("ゲームの Data.p4k が見つかりません。設定タブのゲームのフォルダとチャンネルを確かめてください。");
            var cat = await StationHangarCatalog.GetAsync(stamp, _cts.Token);
            // ハンガーの入口 (class と大きさ) は、場所の中身 (DB) から (2D の地図は使わない)
            var entrances = (_stationLayout?.Place?.Hangars ?? new()).Select(h => (h.Class, h.Size)).ToList();
            var choice = cat.Resolve(_shortName, size, entrances, out var note);
            if (choice == null) throw new InvalidOperationException(note);
            var site = "hangar:" + choice.Socpak;
            var dir = Path.Combine(_tempDir, "h" + _hangarSessions.Count);
            if (!_hangarSessions.TryGetValue(site, out var hs))
            {
                var hrec = StationHangars.RecordOf(choice.Socpak);
                var loc = _loc; var ct = _cts.Token;
                hs = await Task.Run(() => new StationView3D.Session(LayoutFor(hrec, stamp, ct), "", loc, terminalLabel: StationTerminalKinds.Label), ct);
                Directory.CreateDirectory(dir);
                await File.WriteAllBytesAsync(Path.Combine(dir, "station.json"), hs.StationJson(), _cts.Token);
                _hangarSessions[site] = hs;
                _hangarDirs[site] = dir;
            }
            Overlay("");
            var name = StationView3D.HangarTitle(_loc, choice.NameKey);
            Post(new { type = "hangarReady", site, data = UrlOf(_hangarDirs[site]), title = name.Length > 0 ? name : title, how = choice.How });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D (新しい方式)] 「{_title}」の{title}を開けませんでした: {ex}");
            Post(new { type = "hangarError", message = ex.Message });
        }
    }
    private readonly Dictionary<string, string> _hangarDirs = new(StringComparer.Ordinal);

    // ===== ［現在位置］ (2026-11-04、利用者の指示「2D 地図は消して」で、2D の地図の［現在位置］を 3D に向け直した) =====
    // ［マップ］タブの［現在位置］と同じ処理 (MapPanel.CaptureCurrentAsync。/showlocation を送って App.Config.MapCurrent を入れ替える)。MainWindow がつなぐ
    public static Func<string, Task<(bool Ok, string Status)>>? CaptureHook { get; set; }
    // 取った座標 (星系の座標) を、この場所のルート (StationRoot.ToLocal) で場所の座標に直す。自転する場所 (都市) は、取った時刻の自転の角度
    // (StationRoot.CorrectedTheta。データの式 + 設定の補正値 MapSpinCorrections) を使い、さらに取った時刻の前後 HereFitSeconds 秒を HereFitStep 秒ずつずらして、
    // 点が区画の箱 (一覧の置かれた所の箱のうち、大きさ HereBoxMax m 未満のもの) の中に入る時刻のうち、取った時刻にいちばん近いものを使う
    // (2D の地図は床の上に入る時刻を探していた (FitStanding)。3D は床の判定を前もって持たないので、区画の箱で代える。1 秒で点が数百 m〜2 km 動くため)。
    // ビューアには {type:'here', ok, pos, note} を返す (pos は場所の座標)
    private const double HereFitSeconds = 1.5, HereFitStep = 0.02, HereBoxMax = 500;
    private StationRoot? _root;
    private async Task HereAsync()
    {
        try
        {
            var status = "";
            if (CaptureHook is { } hook)
            {
                var (ok, st) = await hook("ステーションの 3D (新しい方式)");
                status = st;
                if (!ok) { Post(new { type = "here", ok = false, note = $"現在位置を取れませんでした: {st}" }); return; }
            }
            var cur = App.Config.MapCurrent;
            if (cur.Utc <= 0) { Post(new { type = "here", ok = false, note = "現在位置がまだありません (［マップ］タブの［現在位置］で取ってください)" }); return; }
            if (Ship3DExports.CurrentStamp() is not { } stamp) { Post(new { type = "here", ok = false, note = "ゲームの Data.p4k が見つかりません" }); return; }
            var record = _record; var ct = _cts.Token;
            _root ??= await Task.Run(() =>
            {
                if (StationHangars.IsRecord(record)) return StationHangars.RootOf(record);
                using var index = StationIndex.Build(stamp.Path, ct);
                var places = StationPlaces.LoadPlaces(DetailPanel.DbPath()).Select(p => p.Place).ToList();
                return index.FindRoots(places, ct: ct).Roots.TryGetValue(record, out var r) ? r : null;
            }, ct);
            if (_root is not { } root) { Post(new { type = "here", ok = false, note = "ゲームデータの中にこの場所のルートが見つかりませんでした" }); return; }
            var global = (cur.X, cur.Y, cur.Z);
            var note = "";
            (double X, double Y, double Z) local;
            if (!root.Spins) local = root.ToLocal(global);
            else
            {
                var body = BodyRecordOfUuid(root.SpinUuid);
                if (body == null) { Post(new { type = "here", ok = false, note = "自転する天体が星図に見つからないので、現在位置を出せません" }); return; }
                var corr = App.Config.MapSpinCorrections;
                var boxes = (_stationLayout?.Instances ?? new()).Where(i => i.Lo != null && i.Hi != null && i.Hi[0] - i.Lo[0] < HereBoxMax && i.Hi[1] - i.Lo[1] < HereBoxMax && i.Hi[2] - i.Lo[2] < HereBoxMax).ToList();
                bool InBox((double X, double Y, double Z) q) => boxes.Any(i => q.X >= i.Lo![0] && q.X <= i.Hi![0] && q.Y >= i.Lo[1] && q.Y <= i.Hi[1] && q.Z >= i.Lo[2] && q.Z <= i.Hi[2]);
                (double X, double Y, double Z)? best = null; double bestDt = 0;
                for (double k = 0; k <= HereFitSeconds + 1e-9 && best == null; k += HereFitStep)
                    foreach (var dt in k == 0 ? new[] { 0.0 } : new[] { -k, k })
                    {
                        if (root.CorrectedTheta(cur.Utc + dt, body, corr) is not { } th) continue;
                        var q = root.ToLocal(global, StationIndex.DefaultRule, th);
                        if (InBox(q)) { best = q; bestDt = dt; break; }
                    }
                if (best is { } b) { local = b; if (bestDt != 0) note = $"（取った時刻を {bestDt:+0.00;-0.00} 秒ずらして区画の中に合わせました）"; }
                else if (root.CorrectedTheta(cur.Utc, body, corr) is { } th0) { local = root.ToLocal(global, StationIndex.DefaultRule, th0); note = "（区画の中に合わせられませんでした。取った時刻のままの位置です）"; }
                else { Post(new { type = "here", ok = false, note = "自転の角度が分からないので、現在位置を出せません" }); return; }
            }
            Post(new { type = "here", ok = true, pos = new[] { local.X, local.Y, local.Z }, note = (status.Length > 0 ? status + " " : "") + note });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D (新しい方式)] 「{_title}」の現在位置を出せませんでした: {ex}");
            Post(new { type = "here", ok = false, note = $"現在位置を出せませんでした: {ex.Message}" });
        }
    }

    // 自転する天体の uuid → 星図の record 名 (gamedata_cache.db の starmap_locations を読み取り専用で引く)。引けなければ null
    private static string? BodyRecordOfUuid(string uuid)
    {
        try
        {
            var db = DetailPanel.DbPath();
            if (!File.Exists(db)) return null;
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT record_name FROM starmap_locations WHERE lower(uuid) = $u LIMIT 1";
            cmd.Parameters.AddWithValue("$u", uuid.ToLowerInvariant());
            return cmd.ExecuteScalar() as string;
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { return null; }
    }
    private StationView3D.Layout? _stationLayout;

    // 区画の置き方の一覧と場所の中身 (view3d_layout.db。StationView3DLayout が裏で一括に作る)。裏の糸から呼ぶ。
    // DB がまだ今の Data.p4k の印でなければ (裏で作っている途中・まだ始まっていない)、裏の一括を始めて (動いていればそれに合流して) 終わるまで待つ。
    // 待つ間は画面の真ん中に大きく「準備中…（済んだ数 / 全部）」を出す。ハンガーの中 (StationHangars の record) は一括に入らないので、無ければその 1 か所を作って DB に足す
    private StationView3D.Layout LayoutFor(string record, Ship3DExports.P4kStamp stamp, CancellationToken ct)
    {
        var db = StationView3DLayout.DbPath;
        var key = StationView3DLayout.GameKey(stamp);
        if (StationView3D.ReadLayoutDb(db, key, record) is { } hit) return hit;
        if (!StationView3D.LayoutDbIsCurrent(db, key))
        {
            var task = StationView3DLayout.Start("3D の窓を開いた");
            while (!task.Wait(500, ct))
            {
                var (d, n) = StationView3DLayout.Progress;
                Dispatcher.BeginInvoke(() => Overlay($"準備中…\n区画の置き方の一覧と場所の中身を、全部の場所の分まとめて作っています ({d} / {n} か所)"));
            }
            if (StationView3D.ReadLayoutDb(db, key, record) is { } hit2) return hit2;
        }
        if (!StationHangars.IsRecord(record)) throw new InvalidOperationException("ゲームデータの中にこの場所が見つかりませんでした" + (StationView3DLayout.LastError is { } e ? $" ({e})" : ""));
        Dispatcher.BeginInvoke(() => Overlay("準備中…\nハンガーの中の区画の置き方の一覧を作っています"));
        lock (StationView3DLayout.PutLock)
        {
            using var index = StationIndex.Build(stamp.Path, ct);
            var layout = StationView3D.LayoutOf(index, StationTree.Load(index, StationHangars.RootOf(record), contents: true, ct: ct));
            StationView3D.PutLayoutDb(db, key, record, layout);
            return layout;
        }
    }

    // 取り出しを 1 本始めてよくなるまで待つ (クラスの説明)。返した IDisposable で返す
    private async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var got = await Dispatcher.InvokeAsync(() =>
            {
                if (!_ownInUse && !Ship3DExports.CpuHighNow) { _ownInUse = true; return 1; }
                if (_ownInUse && Ship3DExports.Instance.TryAcquireExtraRun()) return 2;
                return 0;
            });
            if (got == 1) return new Release(() => Dispatcher.BeginInvoke(() => _ownInUse = false));
            if (got == 2) return new Release(() => Dispatcher.BeginInvoke(() => Ship3DExports.Instance.ReleaseExtraRun()));
            await Task.Delay(500, ct);
        }
    }

    private sealed class Release(Action a) : IDisposable
    {
        private Action? _a = a;
        public void Dispose() { Interlocked.Exchange(ref _a, null)?.Invoke(); }
    }

    private void DeleteTemp()
    {
        if (_tempDir.Length == 0) return;
        var d = _tempDir;
        // 取り出し中の処理が終わるのを少し待ってから消す (20 秒たっても消せなければ残る)
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 20; i++)
            {
                try { if (Directory.Exists(d)) Directory.Delete(d, true); return; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Task.Delay(1000); }
            }
        });
    }
}

// 区画の置き方の一覧と場所の中身の DB (view3d_layout.db) を裏で一括に作る (2026-11-04、利用者の決定「裏でやっとく」)。
//   いつ: アプリの起動の後 (Ship3DExports の自動の取り直しと同じ時: 起動の後 AutoRefreshDelay たってから) と、Data.p4k の印が変わったとき。
//   DB が今の印で作ったものなら何もしない。動いている間に呼ばれたら、動いているものを返す (同時に 1 本だけ)。
//   作り終える前に 3D の窓を開いたときは、窓がこれに合流して、終わるまで「準備中…（済んだ数 / 全部）」を出して待つ (StationView3DWindow.LayoutFor)。
//   作るもの: 星図の場所の表 (ゲームデータの DB の starmap_locations) をルート探しして見つかった場所の全部 (StationView3D.BuildLayoutDb)
//   同梱 (2026-11-04、利用者の指示「DBに入れたものは配れる。時価データでなくアドレスだから」): インストール先 (AppContext.BaseDirectory) に view3d_layout.db があり、
//   その印が今のゲームの版と同じなら、作らずに置き場所へ写す (起動の後・窓を開いたとき)。印が違えば (パッチが来た) 裏で作り直す。
//   印 (GameKey) は、どの PC でも同じになるよう、Data.p4k と同じフォルダの build_manifest.id の Branch・Version・RequestedP4ChangeNum にする
//   (読めなければ、その PC の Data.p4k の印 (パス・大きさ・更新日時)。その DB は配っても使われない)
internal static class StationView3DLayout
{
    public static string DbPath => Path.Combine(StationPlaces.Folder, StationView3D.LayoutDbName);
    // 一括で並べる最大の本数 (試しの道具で全部の場所を作って計った値: 1 本 347 秒・メモリのピーク 3.5 GB、4 本 355 秒・3.1 GB。門で待つので速くはならないが、メモリのピークは下がった)
    public const int LayoutParallelMax = 4;
    public static string BundledPath => Path.Combine(AppContext.BaseDirectory, StationView3D.LayoutDbName);

    // 前の版の 2D の地図と古い方式のステーションの 3D のファイルを消す (2026-11-04、利用者の指示「2D 地図は消して」「他のユーザーも更新時に消す仕組みで」)。
    // いつ: アプリの起動の後 (MainWindow が裏で呼ぶ)。置き場所は設定の 3D の置き場所 (Ship3DExports.Folder)。
    // 消すもの (ファイルの名前の決まりで見分ける):
    //   stations の直下の *.json (2D の地図)・*.meshes.gz・*.meshes.keys (2D の地図の読み取りの保存)・*.layout.json (前の区画の置き方の一覧)。view3d_layout.db* は消さない
    //   stations\hangars の *.json・*.meshes.gz・*.meshes.keys (ハンガーの中の 2D の地図)。ハンガーの表 hangar_catalog.json は 3D で使うので消さない
    //   stations3d フォルダ (古い方式のステーションの 3D) を丸ごと
    // 全部消せたら、作業ディレクトリに印のファイル (OldMapsCleanedMark) を書いて、次からは消しに行かない。消せなかったもの (使用中など) があれば印を書かず、次の起動でまた試す
    public const string OldMapsCleanedMark = "old_station_maps_removed.txt";
    public static void CleanupOldMaps()
    {
        var mark = Path.Combine(App.Config.WorkingDirectory, OldMapsCleanedMark);
        try { if (File.Exists(mark)) return; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        int removed = 0, failed = 0; long bytes = 0;
        var names = new List<string>();
        void Del(FileInfo f)
        {
            try { var len = f.Length; f.Delete(); removed++; bytes += len; if (names.Count < 20) names.Add(f.Name); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed++; Log($"前の版の地図のファイルを消せませんでした (次の起動でまた試します): {f.FullName}: {ex.Message}"); }
        }
        static bool Is2DFile(string name) => name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".meshes.gz", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".meshes.keys", StringComparison.OrdinalIgnoreCase);
        try
        {
            var st = new DirectoryInfo(StationPlaces.Folder);
            if (st.Exists)
                foreach (var f in st.GetFiles())
                    if (Is2DFile(f.Name)) Del(f);
            var hg = new DirectoryInfo(StationPlaces.HangarFolder);
            if (hg.Exists)
                foreach (var f in hg.GetFiles())
                    if (Is2DFile(f.Name) && !f.Name.Equals(StationHangarCatalog.FileName, StringComparison.OrdinalIgnoreCase)) Del(f);
            var old3d = new DirectoryInfo(Path.Combine(Ship3DExports.Folder, "stations3d"));
            if (old3d.Exists)
            {
                foreach (var f in old3d.GetFiles("*", SearchOption.AllDirectories)) Del(f);
                if (failed == 0) try { old3d.Delete(true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed++; Log($"古い方式のステーションの 3D のフォルダを消せませんでした: {ex.Message}"); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed++; Log($"前の版の地図のファイルを消せませんでした: {ex.Message}"); }
        if (removed > 0) Log($"前の版の 2D の地図・古い方式のステーションの 3D のファイルを {removed} 個 ({bytes / 1048576.0:F1} MB) 消しました: {string.Join(", ", names)}{(removed > names.Count ? " ほか" : "")}");
        if (failed == 0)
            try { File.WriteAllText(mark, $"{DateTime.Now:o} 消したファイル {removed} 個 ({bytes} バイト)\n"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log($"消した印を書けませんでした (次の起動でもう一度見ます): {ex.Message}"); }
    }
    public static string GameKey(Ship3DExports.P4kStamp stamp)
    {
        var dir = Path.GetDirectoryName(stamp.Path) ?? "";
        var (version, change, branch) = GameDataExtractor.ReadBuildManifestFile(Path.Combine(dir, "build_manifest.id"));
        return version != null && change != null ? $"build|{branch}|{version}|{change}" : "p4k|" + stamp.Key;
    }
    // 同梱の DB が今の版のものなら、置き場所へ写す (写したら true)
    private static bool CopyBundled(string key)
    {
        try
        {
            if (!File.Exists(BundledPath) || !StationView3D.LayoutDbIsCurrent(BundledPath, key)) return false;
            Directory.CreateDirectory(StationPlaces.Folder);
            var tmp = DbPath + ".copy";
            File.Copy(BundledPath, tmp, true);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Move(tmp, DbPath, true);
            Log($"同梱の区画の置き方の一覧 (版 {key}) を置き場所へ写しました");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log($"同梱の区画の置き方の一覧を写せませんでした: {ex.Message}"); return false; }
    }
    public static readonly object PutLock = new();
    private static readonly object Gate = new();
    private static Task? _task;
    private static string _taskKey = "";
    private static int _done, _total;
    public static (int Done, int Total) Progress => (Volatile.Read(ref _done), Volatile.Read(ref _total));
    public static string? LastError { get; private set; }
    private static void Log(string m) => App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D (新しい方式)] {m}");

    // 動いているか (設定タブの［マップをまとめて取り込む］の表示に使う)。Changed は始めた・済んだときに (どの糸からでも) 呼ぶ
    public static bool Running { get { lock (Gate) return _task != null && !_task.IsCompleted; } }
    public static event Action? Changed;
    public static bool IsCurrent() => Ship3DExports.CurrentStamp() is { } st && StationView3D.LayoutDbIsCurrent(DbPath, GameKey(st));
    // DB の問題 (2026-11-04、利用者の指示「問題あれば有効、なければ無効」。設定タブの［マップをまとめて取り込む］の有効・無効と横の文)。問題が無ければ null。
    // 問題: Data.p4k が見つからない・DB が無い・今の Data.p4k の版で作っていない・作れなかった場所がある・作りかけ (.tmp) が残っている
    public static string? Problem()
    {
        if (Ship3DExports.CurrentStamp() is not { } st) return "ゲームの Data.p4k が見つかりません";
        if (!File.Exists(DbPath)) return File.Exists(DbPath + ".tmp") ? "作りかけで止まっています" : "まだ作っていません";
        if (!StationView3D.LayoutDbIsCurrent(DbPath, GameKey(st))) return "今のパッチの版で作っていません";
        if (int.TryParse(StationView3D.LayoutDbMeta(DbPath, "failed"), out var f) && f > 0) return $"作れなかった場所が {f} か所あります";
        if (File.Exists(DbPath + ".tmp") && !Running) return "作りかけで止まっています";
        return null;
    }

    // force: 今の版の DB があっても作り直す (設定タブの［マップをまとめて取り込む］)
    public static Task Start(string why, bool force = false)
    {
        if (Ship3DExports.CurrentStamp() is not { } stamp) return Task.CompletedTask;
        var key = GameKey(stamp);
        lock (Gate)
        {
            if (_task != null && !_task.IsCompleted && _taskKey == key) return _task;
            if (!force)
            {
                if (StationView3D.LayoutDbIsCurrent(DbPath, key)) return Task.CompletedTask;
                lock (PutLock) if (CopyBundled(key)) return Task.CompletedTask;
            }
            _taskKey = key; _done = 0; _total = 0; LastError = null;
            var job = NewJob();
            _task = Task.Factory.StartNew(() => Build(stamp, why, force, job), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        Changed?.Invoke();
        return _task;
    }

    // 進み具合の欄 (Ship3DProgressPanel。船の書き出しと同じ欄) に出す 1 行 (2026-11-04、利用者の指摘「バーが出ない」)。取り消しのボタンで止められる (job.Cts)。
    // ％と残り (2026-11-04、利用者の指摘「予想が11分まで伸びた。予想とは？」で、場所の数でなく重さで見るように直した):
    //   予想の総時間 (EstimatedSeconds) を、済んだ重さの割合 f (BuildLayoutDb の weightProgress。場所ごとの前の秒、無ければ置かれた所の数から見込んだ重さ) が
    //   WeightMin 未満の間は前に作ったときの秒 (DB の meta の seconds。無ければ FirstGuessSeconds)、その後は「経過 ÷ f」にして、Ship3DJob の「経過 ÷ (経過 + 残り)」で出す。
    //   重い場所から始めるので、最後に長い場所が残って予想が伸び続けることが少なくなる
    private const double FirstGuessSeconds = 350;
    private static Ship3DJob? NewJob()
    {
        var disp = Application.Current?.Dispatcher;
        if (disp == null) return null;
        var job = new Ship3DJob("view3d_layout", "view3d-layout", "区画の置き方の一覧", null) { Kind = Ship3DJobKind.Station, LogTag = "区画の置き方の一覧" };
        job.EstimatedSeconds = PreviousSeconds() ?? FirstGuessSeconds;
        disp.Invoke(() => { Ship3DExports.Instance.AddJob(job); job.Watch.Start(); job.SetPhase(Ship3DJobPhase.Exporting, "区画の置き方の一覧を作っています"); });
        return job;
    }
    // 前に作ったときにかかった秒 (DB の meta の seconds)。無ければ null
    private static double? PreviousSeconds()
    {
        try
        {
            if (!File.Exists(DbPath)) return null;
            using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath};Mode=ReadOnly;Pooling=False");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key = 'seconds'";
            return double.TryParse(cmd.ExecuteScalar() as string, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { return null; }
    }
    private const double WeightMin = 0.02;
    private static void JobProgress(Ship3DJob? job, double f)
    {
        if (job == null) return;
        var e = job.Watch.Elapsed.TotalSeconds;
        if (f >= WeightMin) job.EstimatedSeconds = e / f;
        Application.Current?.Dispatcher.BeginInvoke(job.Refresh);
    }
    private static void JobEnd(Ship3DJob? job, Ship3DJobPhase phase)
    {
        if (job == null) return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            job.Watch.Stop();
            job.SetPhase(phase, "");
            Ship3DExports.Instance.RemoveJob(job);
        });
    }

    private static void Build(Ship3DExports.P4kStamp stamp, string why, bool force, Ship3DJob? job)
    {
        var ct = job?.Cts.Token ?? CancellationToken.None;
        var phase = Ship3DJobPhase.Done;
        try
        {
            lock (PutLock)
            {
                var key = GameKey(stamp);
                if (!force && StationView3D.LayoutDbIsCurrent(DbPath, key)) return;
                Log($"区画の置き方の一覧と場所の中身を裏で一括に作ります ({why})");
                var places = StationPlaces.LoadPlaces(DetailPanel.DbPath()).Select(p => p.Place).ToList();
                using var index = StationIndex.Build(stamp.Path);
                var roots = index.FindRoots(places).Roots;
                Directory.CreateDirectory(StationPlaces.Folder);
                // 並べる数: 船の書き出しと同じ決まり (Ship3DExports.MaxParallelExports: CPU の数の半分と空きメモリ ÷ 3 GB の小さいほう) で、最大 LayoutParallelMax 本。
                // メモリは BuildLayoutDb の門 (StationView3D.LayoutMemGate) で 4 GB 以下に抑える (親の決定)。1 か所を始める前に、CPU 使用率が 90% 以上なら下がるまで待つ
                var par = Math.Min(LayoutParallelMax, Ship3DExports.MaxParallelExports().Limit);
                var res = StationView3D.BuildLayoutDb(DbPath, key, index, roots, (d, n) => { Volatile.Write(ref _done, d); Volatile.Write(ref _total, n); }, ct, par,
                    c => { while (Ship3DExports.CpuHighNow) Task.Delay(1000, c).Wait(c); }, f => JobProgress(job, f),
                    fillLog: m => App.AppendStartupLog(App.Config.WorkingDirectory, $"[床の補い] {m}"));
                Log($"並べた数 {par}");
                Log($"一括で作りました: 場所 {res.Places}・置かれた所 {res.Instances}・socpak {res.Socpaks}・{res.Bytes / 1024} KB・{res.Seconds:F1} 秒" +
                    (res.Failed.Count > 0 ? $"。作れなかった場所 {res.Failed.Count}: {string.Join(" / ", res.Failed)}" : ""));
            }
        }
        catch (OperationCanceledException)
        {
            phase = Ship3DJobPhase.Cancelled;
            LastError = "取り消しました";
            Log("一括で作るのを取り消しました (前の DB はそのまま)");
            try { File.Delete(DbPath + ".tmp"); } catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException) { }
        }
        catch (AggregateException ae) when (ae.InnerExceptions.All(x => x is OperationCanceledException))
        {
            phase = Ship3DJobPhase.Cancelled;
            LastError = "取り消しました";
            Log("一括で作るのを取り消しました (前の DB はそのまま)");
            try { File.Delete(DbPath + ".tmp"); } catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception ex)
        {
            phase = Ship3DJobPhase.Failed;
            LastError = ex.Message;
            Log($"一括で作れませんでした: {ex}");
        }
        finally { JobEnd(job, phase); _ = Task.Run(() => Changed?.Invoke()); }
    }
}
