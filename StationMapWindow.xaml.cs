using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace StarCitizenJapaneseTextCreater;

// ステーションの階層マップの別ウィンドウ (モードレス)。
//   左: 星系 → 種類のチェック → ステーション (星図の場所。種類は ステーション・都市 (Manmade 系・LandingZone)・前哨基地 (Outpost 系)・小惑星 (Asteroid 系)。
//       種類とは別に「名前の無い場所」(名前が record 名のまま) のチェックがあり、初めは外して出さない。
//       取り込んでいない場所には「（未取り込み）」を付ける。同じ星系に同じ名前の場所が 2 つ以上あれば、親の場所のゲームの名前を「（親）」で添える)。
//   右: StationMapPanel (階・区画のリスト、道案内、地図)。場所の木 (ShopTree) は［詳細］から渡されたものを使い、無ければ DB から作る
public partial class StationMapWindow : Window
{
    // 一覧の行: 場所 1 か所 (Record あり)、または同じ地図のまとまりの行 (IsGroup。Group はまとまりの鍵)。InGroup はまとまりの中の行か
    private sealed record Row(string Record, string Name, string Label, string Display, string? Group = null, bool IsGroup = false, bool InGroup = false);

    // 同じ地図を使いまわしている場所のまとまり (StationContentKey。ルートより下の中身の鍵)。Data.p4k の索引とルート探しで決める (数秒)。
    // Data.p4k が同じ間は使い回す (アプリを動かしている間だけ)。record 名 → 鍵 (ルートの見つからない場所は入らない)
    private static (Ship3DExports.P4kStamp Stamp, Dictionary<string, string> Keys)? s_contentKeys;
    private static Task<(Ship3DExports.P4kStamp Stamp, Dictionary<string, string> Keys)>? s_keysTask;
    private Dictionary<string, string>? _keys;
    // 開いているまとまりの鍵 (この窓の間だけ。初めは全部閉じる)
    private readonly HashSet<string> _openGroups = new(StringComparer.Ordinal);
    // 見えるようにする場所 (［詳細］から開いた場所)。その場所がまとまりの中なら、そのまとまりを開いて (1 回だけ) この値を消す
    private string? _reveal;

    // 種類のチェックの種類 (表の順)。名前は［詳細］の場所の種類 (ShopTree.TypeLabels) に合わせる
    private const string KindStation = "ステーション・都市", KindOutpost = "前哨基地", KindAsteroid = "小惑星";
    // (2026-11-01、利用者の指示「ミッションで使われるマップは全部出すべき」・「1 A」) 足した種類。言葉は［詳細］の場所の種類 (ShopTree.TypeLabel) と、
    // 星図に無い種類のゲームの訳 (StationPlace.ExtraTypeLabels: 洞窟・残骸・墜落現場)
    private const string KindCave = "洞窟", KindWreck = "残骸", KindCrash = "墜落現場", KindPoi = "注目地点", KindAnomaly = "異常域", KindJump = "ジャンプポイント", KindNav = "航行ポイント";
    // 洞窟・残骸・墜落現場は、今の床の決め方では床ができず空の地図になるので、v2.5.0 では一覧にもチェックにも出さない (2026-11-01、利用者「次バージョンでもよい」)。
    // ルート (FindRoots の規則 1) は見つけたままにしてあり、HiddenKinds から外せば出る
    private static readonly HashSet<string> HiddenKinds = new(StringComparer.Ordinal) { KindCave, KindWreck, KindCrash };
    // 星図に載っていない場所のうち、床のメッシュの無い場所を一覧に出さない (BuildContentKeys。次の版で床の作り方を足すときに false にする)
    internal const bool HideNoFloorPlaces = true;
    // 床のメッシュの無い星図の場所 (ステーション・都市・前哨基地など) の record (2026-11-01、利用者の決定: 星図外と同じく、床のできない場所は出さない。BuildContentKeys で作る)
    private static HashSet<string> s_noFloor = new(StringComparer.Ordinal);
    private static readonly string[] Kinds = [KindStation, KindOutpost, KindAsteroid, KindPoi, KindAnomaly, KindJump, KindNav];
    // 名前の無い場所 (ゲームのデータに名前が無く、名前が record 名のままの場所。HasGameName が false)。種類とは別の軸で、付けたときだけ一覧に出す (内部名のまま)。
    // 「全部」は種類とこれの両方を付ける・外す。覚え方は種類と同じ (s_excludedKinds に入れる)
    private const string KindUnnamed = "名前の無い場所";
    // 含めない種類。武器の表の種類の絞り込み (FilterSortGrid の _excluded) と同じく、アプリを動かしている間だけ覚える (設定には書かない)。
    // 窓を閉じて開き直しても残るように static にする。最初は ステーション・都市 だけ (名前の無い場所は外す)
    private static readonly HashSet<string> s_excludedKinds = new(StringComparer.Ordinal)
        { KindOutpost, KindAsteroid, KindCave, KindWreck, KindCrash, KindPoi, KindAnomaly, KindJump, KindNav, KindUnnamed };
    // 星図に載っていない場所 (StationPlace.IsUnlisted。FindRoots で見つけたもの): record・名前・種類のチェック・星系・中身の鍵。BuildContentKeys で作る
    private sealed record UnlistedPlace(string Record, string Name, string Label, string Kind, string System);
    private static List<UnlistedPlace> s_unlisted = new();
    private CheckBox? _allKinds;
    private readonly List<(string Kind, CheckBox Box)> _kindBoxes = new();
    private bool _kindUpdating;

    private ShopTree? _tree;
    // 窓を閉じた (Closed の後。裏の待ちから戻ったときに何もしないため)
    private bool _closed;
    private List<Row> _rows = new();
    private bool _suppress;
    private string? _record;

    public StationMapWindow(string? record, string name, ShopTree? tree)
    {
        InitializeComponent();
        _tree = tree;
        BuildKindBoxes();
        btnImport.IsEnabled = false;
        var ex = StationMapExports.Instance;
        Action onState = () => { UpdateBar(); };
        System.ComponentModel.PropertyChangedEventHandler onProp = (_, e) => { if (e.PropertyName == nameof(Ship3DExports.OverallText)) UpdateBar(); };
        ex.StateChanged += onState;
        Ship3DExports.Instance.StateChanged += onState;
        Ship3DExports.Instance.PropertyChanged += onProp;
        Closed += (_, _) =>
        {
            _closed = true;
            ex.StateChanged -= onState;
            Ship3DExports.Instance.StateChanged -= onState;
            Ship3DExports.Instance.PropertyChanged -= onProp;
            panel.DisposeWeb();
        };
        Loaded += async (_, _) =>
        {
            if (_tree == null)
            {
                txtStatus.Text = "読み込み中...";
                try
                {
                    var db = DetailPanel.DbPath();
                    var trade = DetailPanel.TradeDbPath();
                    _tree = await Task.Run(() =>
                    {
                        if (!File.Exists(db)) return (ShopTree?)null;
                        using var q = GameDataQueryService.OpenReadOnly(db);
                        return q.BuildShopTree(trade);
                    });
                }
                catch (Exception ex)
                {
                    txtStatus.Text = $"場所の一覧を読み込めませんでした: {ex.Message}";
                    App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの地図] 別ウィンドウの場所の一覧を読み込めませんでした: {ex}");
                    return;
                }
                txtStatus.Text = "";
            }
            if (_tree == null || _tree.IsEmpty) { txtStatus.Text = "場所の一覧がありません (設定タブの［インデックス構築］)"; return; }
            var systems = _tree.Systems.Select(s => s.System).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            var start = record != null ? _tree.NodeByStarmapRecord(record) : null;
            _suppress = true;
            try
            {
                lstSystems.ItemsSource = systems;
                lstSystems.SelectedItem = start?.System ?? systems.FirstOrDefault();
            }
            finally { _suppress = false; }
            // ［詳細］から開いた場所: 一覧でその行を選び、まとまりの中ならそのまとまりを開く (_reveal。同じ地図の判定ができた後にも効く)
            if (start != null) { _record = start.RecordName; _reveal = start.RecordName; }
            RefreshStations();
            var keysTask = LoadContentKeysAsync();
            if (start != null) await SelectAsync(start.RecordName, start.Name);
            else if (record != null && record.StartsWith(StationPlace.UnlistedPrefix, StringComparison.Ordinal))
            {
                // 星図に載っていない場所 (Unlisted.<guid>・Unlisted.<元の record>.<番号>。契約の詳細の地図のリンクから開いたとき)。
                // 一覧の行 (s_unlisted) は同じ地図の判定 (BuildContentKeys) で作るので、それを待ってから星系とその行を選ぶ。
                // 一覧に無ければ (判定ができなかった等) 星系はそのままで地図だけを出す
                // 待っている間に窓を閉じた・一覧で別の場所を選んだときは、何もしない (選び直さない)
                await keysTask;
                if (_closed || _record != null) return;
                if (s_unlisted.FirstOrDefault(u => u.Record == record) is { } u)
                {
                    _suppress = true;
                    try { lstSystems.SelectedItem = systems.FirstOrDefault(s => string.Equals(s, u.System, StringComparison.OrdinalIgnoreCase)) ?? lstSystems.SelectedItem; }
                    finally { _suppress = false; }
                    _record = u.Record;
                    _reveal = u.Record;
                    RefreshStations();
                    await SelectAsync(u.Record, u.Name);
                }
                else await SelectAsync(record, name);
            }
        };
    }

    // 場所の種類 (種類のチェックの名前)。一覧に出さない種類なら null
    private static string? KindOf(ShopTreeNode n) => KindOfType(n.Type);
    private static string? KindOfType(string type) =>
        type.StartsWith("Manmade", StringComparison.Ordinal) || type == "LandingZone" ? KindStation
        : type is "Outpost" or "Outpost_InvalidQT" ? KindOutpost
        : type is "Asteroid" or "Asteroid_ValidQT" ? KindAsteroid
        : type == StationPlace.TypeCave ? KindCave
        : type == StationPlace.TypeWreck ? KindWreck
        : type == StationPlace.TypeCrashSite ? KindCrash
        : type == "PointOfInterest" ? KindPoi
        : type == "Anomaly" ? KindAnomaly
        : type == "JumpPoint" ? KindJump
        : type == "NavPoint" ? KindNav
        : null;

    // ゲームの名前がある場所か (星図の名前が引けず、名前が record 名 (StarMapObject. の後ろ) のままの場所は内部名なので親として添えない)
    private static bool HasGameName(ShopTreeNode n)
    {
        var dot = n.RecordName.IndexOf('.');
        var shortName = dot >= 0 ? n.RecordName[(dot + 1)..] : n.RecordName;
        return n.Name.Length > 0 && !string.Equals(n.Name, shortName, StringComparison.Ordinal);
    }

    // 種類のチェックを作る (FilterSortGrid.OpenFilter の「全て」と値ごとのチェックと同じ作り・同じ操作。件数は選んでいる星系の場所の数)
    private void BuildKindBoxes()
    {
        _allKinds = new CheckBox { Content = "全部", Margin = new Thickness(0, 0, 0, 2) };
        pnlKinds.Children.Add(_allKinds);
        foreach (var kind in Kinds.Append(KindUnnamed))
        {
            var box = new CheckBox { Content = kind, IsChecked = !s_excludedKinds.Contains(kind), Margin = new Thickness(0, 1, 0, 1) };
            var k = kind;
            box.Checked += (_, _) => { if (_kindUpdating) return; s_excludedKinds.Remove(k); RefreshStations(); };
            box.Unchecked += (_, _) => { if (_kindUpdating) return; s_excludedKinds.Add(k); RefreshStations(); };
            _kindBoxes.Add((kind, box));
            pnlKinds.Children.Add(box);
        }
        _allKinds.Click += (_, _) =>
        {
            var check = _allKinds.IsChecked == true;
            _kindUpdating = true;
            foreach (var (kind, box) in _kindBoxes)
            {
                box.IsChecked = check;
                if (check) s_excludedKinds.Remove(kind); else s_excludedKinds.Add(kind);
            }
            _kindUpdating = false;
            RefreshStations();
        };
        UpdateAllKinds();
    }

    // 「全部」の印: 全部入っていればチェック、全部外れていれば空、混ざっていれば中間
    private void UpdateAllKinds()
    {
        if (_allKinds == null) return;
        _kindUpdating = true;
        _allKinds.IsChecked = _kindBoxes.All(b => b.Box.IsChecked == true) ? true
            : _kindBoxes.All(b => b.Box.IsChecked != true) ? false : null;
        _kindUpdating = false;
    }

    // 同じ地図の鍵を (無ければ裏で) 作り、できたら一覧を作り直す。作れなければ (Data.p4k が無い等) まとめずに出す (状態の欄に知らせる)
    private async Task LoadContentKeysAsync()
    {
        var stamp = Ship3DExports.CurrentStamp();
        if (stamp == null) { txtGroupStatus.Text = "Data.p4k が見つからないので、同じ地図のまとめはしていません"; return; }
        if (s_contentKeys is { } c && c.Stamp == stamp) { _keys = c.Keys; RefreshStations(); return; }
        txtGroupStatus.Text = "同じ地図の場所を調べています...";
        try
        {
            var task = s_keysTask;
            if (task == null || task.IsFaulted || task.IsCompletedSuccessfully && task.Result.Stamp != stamp)
                s_keysTask = task = Task.Run(() => BuildContentKeys(stamp));
            var res = await task;
            if (res.Stamp == stamp) { s_contentKeys = res; _keys = res.Keys; }
            txtGroupStatus.Text = "";
        }
        catch (Exception ex)
        {
            txtGroupStatus.Text = $"同じ地図の場所を調べられなかったので、まとめずに出しています: {ex.Message}";
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの地図] 同じ地図の判定ができませんでした: {ex}");
        }
        RefreshStations();
    }

    // Data.p4k の索引 (StationIndex.Build) と星図の場所 (StationMapExports.LoadPlaces) のルート探し (FindRoots) から、record 名 → 中身の鍵 (StationContentKey.Of)
    private static (Ship3DExports.P4kStamp Stamp, Dictionary<string, string> Keys) BuildContentKeys(Ship3DExports.P4kStamp stamp)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rows = StationMapExports.LoadPlaces(DetailPanel.DbPath());
        var places = rows.Select(p => p.Place).ToList();
        using var idx = StationIndex.Build(stamp.Path);
        var search = idx.FindRoots(places);
        var keys = search.Roots.ToDictionary(kv => kv.Key, kv => StationContentKey.Of(kv.Value), StringComparer.Ordinal);
        // 星図に載っていない場所の行: 名前は、同じ socpak の 2 つ目からの置き場ならその星図の場所の名前、ほかは種類の言葉 (データに名前が無いので作らない)。
        // 添え字は、その場所が乗っている天体 (自転の天体の uuid → 星図の場所) の名前
        var byUuid = rows.Where(r => r.Place.Uuid.Length > 0).GroupBy(r => r.Place.Uuid.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var byRecord = rows.GroupBy(r => r.Place.Record).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var unlisted = new List<UnlistedPlace>();
        // 床のメッシュ (StationFloorBuilder.IsExportMesh) の無い場所は、取り込んでも空の地図になるので、v2.5.0 では出さない (2026-11-01、利用者の決定「A」)。
        // 書き出しの計画 (PlanExports) と同じ調べ方 (socpak の .soc のメッシュ名) を、socpak ごとに 1 回だけする。HideNoFloorPlaces を false にすれば出る。
        // ハーネスで 931 か所 (洞窟・残骸・墜落現場を除く) を調べて 0.8 秒、床のメッシュの無い場所 486
        var hasFloorMesh = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var r in search.Roots.Values.Where(r => r.Place.IsUnlisted))
        {
            if (KindOfType(r.Place.Type) is not { } kind || HiddenKinds.Contains(kind)) continue;
            if (HideNoFloorPlaces && !HasFloorMesh(idx, r, hasFloorMesh)) continue;
            var (name, label) = UnlistedNames(r, rec => byRecord.TryGetValue(rec, out var src) ? src.Name : null,
                uuid => byUuid.TryGetValue(uuid, out var b) ? b.Name : null);
            unlisted.Add(new UnlistedPlace(r.Place.Record, name, label, kind, r.Place.System));
        }
        s_unlisted = unlisted;
        s_noFloor = HideNoFloorPlaces
            ? search.Roots.Values.Where(r => !r.Place.IsUnlisted && !HasFloorMesh(idx, r, hasFloorMesh)).Select(r => r.Place.Record).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの地図] 同じ地図の判定: ルート {keys.Count}、中身の鍵 {keys.Values.Distinct().Count()}、{sw.Elapsed.TotalSeconds:F1} 秒");
        return (stamp, keys);
    }

    // 床のメッシュ (StationFloorBuilder.IsExportMesh) のある場所か。書き出しの計画 (PlanExports) と同じ調べ方 (socpak の .soc のメッシュ名) を、
    // socpak ごとに 1 回だけする (結果は hasFloorMesh に覚える)。一覧 (BuildContentKeys) と契約の地図の表 (GameDataExtractor の mission_map_places) で使う
    internal static bool HasFloorMesh(StationIndex idx, StationRoot r, Dictionary<string, bool> hasFloorMesh)
    {
        var t = StationTree.Load(idx, r, contents: false);
        foreach (var sp in t.Instances.Where(i => i.Found && i.Socpak.Length > 0).Select(i => i.Socpak).Distinct(StringComparer.Ordinal))
        {
            if (!hasFloorMesh.TryGetValue(sp, out var has)) hasFloorMesh[sp] = has = StationFloorBuilder.ScanSocMeshes(idx, sp).Any(StationFloorBuilder.IsExportMesh);
            if (has) return true;
        }
        return false;
    }

    // 星図に載っていない場所の一覧の名前と表示 (Label)。名前は、同じ socpak の 2 つ目からの置き場ならその星図の場所の名前、ほかは種類の言葉
    // (データに名前が無いので作らない)。表示は、その場所が乗っている天体 (自転の天体の uuid → 星図の場所) の名前を（）で添えたもの。
    // nameByRecord: 星図の場所の record → 英語名、nameByUuid: 星図の場所の uuid (小文字) → 英語名 (無ければ null)。
    // 一覧 (BuildContentKeys) と契約の地図の表 (GameDataExtractor の mission_map_places) で使う
    internal static (string Name, string Label) UnlistedNames(StationRoot r, Func<string, string?> nameByRecord, Func<string, string?> nameByUuid)
    {
        var word = StationPlace.ExtraTypeLabels.TryGetValue(r.Place.Type, out var w) ? w : ShopTree.TypeLabel(r.Place.Type);
        var name = r.SharedRecord.Length > 0 && nameByRecord(r.SharedRecord) is { Length: > 0 } src && !src.Contains('_') ? src : word;
        var body = r.SpinUuid.Length > 0 ? nameByUuid(r.SpinUuid.ToLowerInvariant()) ?? "" : "";
        return (name, body.Length > 0 ? $"{name}（{body}）" : name);
    }

    // 一覧に出す場所か (契約の詳細の地図のリンクを、一覧と同じ決まりで出すため。2026-11-01)。種類のチェックのある種類で、
    // 一覧に出さない種類 (HiddenKinds) でなく、星図に載っていない場所 (isUnlisted) なら床のメッシュがあること (HideNoFloorPlaces のとき)。
    // 種類のチェック・名前の無い場所のチェック (利用者が付け外しするもの) は見ない
    internal static bool IsListedPlace(string type, bool isUnlisted, bool hasFloor) =>
        KindOfType(type) is { } kind && !HiddenKinds.Contains(kind) && (!isUnlisted || !HideNoFloorPlaces || hasFloor);

    // まとまりの名前: 名前が全部同じならその名前。違えば、全部の名前に共通する頭 (語の区切り (空白・「-」・「#」) までで切り、終わりの区切りは除く)。
    // 共通する頭が無ければ「名前の順でいちばん前の名前 など」
    internal static string GroupName(IEnumerable<string> names)
    {
        var list = names.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) return "";
        if (list.Count == 1) return list[0];
        var first = list[0];
        var len = first.Length;
        foreach (var n in list.Skip(1))
        {
            var i = 0;
            while (i < len && i < n.Length && n[i] == first[i]) i++;
            len = i;
        }
        static bool Delim(char c) => c is ' ' or '-' or '#';
        // 語の途中で切れていれば、その語の前の区切りまで戻す (全部の名前で、共通の頭の次が区切りか名前の終わりなら、語の切れ目)
        while (len > 0 && !Delim(first[len - 1]) && !list.All(n => n.Length == len || Delim(n[len]))) len--;
        var head = first[..len].TrimEnd(' ', '-', '#');
        return head.Length > 0 ? head : $"{first} など";
    }

    private void RefreshStations()
    {
        UpdateAllKinds();
        if (_tree == null) return;
        var system = lstSystems.SelectedItem as string;
        var q = txtFilter.Text.Trim();
        // 選んでいる星系の、種類のチェックに当たる場所 (チェックの有無によらず全部。件数と同じ名前の判定に使う)
        var places = _tree.Places.Where(n => KindOf(n) != null && string.Equals(n.System, system, StringComparison.OrdinalIgnoreCase) && !s_noFloor.Contains(n.RecordName)
            // 取り込んだら一覧に出る階の無い空の地図だった場所 (StationMapExports.IsEmptyMap。取り込む前には分からない通信アレイ・Rockcracker_Sandbox など) も出さない
            && !(HideNoFloorPlaces && StationMapExports.IsEmptyMap(n.RecordName))).ToList();
        var unlisted = s_unlisted.Where(u => string.Equals(u.System, system, StringComparison.OrdinalIgnoreCase) && !(HideNoFloorPlaces && StationMapExports.IsEmptyMap(u.Record))).ToList();
        foreach (var (kind, box) in _kindBoxes)
            box.Content = $"{kind} ({(kind == KindUnnamed ? places.Count(n => !HasGameName(n)) : places.Count(n => KindOf(n) == kind) + unlisted.Count(u => u.Kind == kind))})";
        // 同じ名前の場所が 2 つ以上ある名前 (チェックで隠れている種類の場所も数える。チェックを変えても添え字が変わらないように)
        var dupNames = places.GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
            .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var showUnnamed = !s_excludedKinds.Contains(KindUnnamed);
        var items = places.Where(n => !s_excludedKinds.Contains(KindOf(n)!) && (showUnnamed || HasGameName(n)))
            .Select(n =>
            {
                var label = dupNames.Contains(n.Name) && n.Parent is { } p && HasGameName(p) ? $"{n.Name}（{p.Name}）" : n.Name;
                var key = _keys != null && _keys.TryGetValue(n.RecordName, out var k) ? k : null;
                return new Row(n.RecordName, n.Name, label, StationMapExports.Instance.StateOf(n.RecordName) == Ship3DCacheState.None ? $"{label}（未取り込み）" : label, key);
            })
            // 星図に載っていない場所 (名前は種類の言葉か、共有元の星図の場所の名前。名前の無い場所のチェックの対象にはしない)
            .Concat(unlisted.Where(u => !s_excludedKinds.Contains(u.Kind)).Select(u =>
            {
                var key = _keys != null && _keys.TryGetValue(u.Record, out var k) ? k : null;
                return new Row(u.Record, u.Name, u.Label, StationMapExports.Instance.StateOf(u.Record) == Ship3DCacheState.None ? $"{u.Label}（未取り込み）" : u.Label, key);
            }))
            .Where(r => q.Length == 0 || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Label.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
        _rows = GroupRows(items, places);
        _suppress = true;
        try
        {
            lstStations.ItemsSource = _rows;
            lstStations.SelectedItem = _rows.FirstOrDefault(r => r.Record == _record);
            if (lstStations.SelectedItem != null) lstStations.ScrollIntoView(lstStations.SelectedItem);
        }
        finally { _suppress = false; }
    }

    // 同じ地図のまとまりにした一覧の行。まとまりは、一覧に残った行 (種類・名前の無い場所・名前の絞り込みの後) のうち同じ鍵が 2 つ以上あるもの。
    // まとまりの名前は、その星系の同じ鍵の場所全部 (チェック・絞り込みの前) の名前から決める (GroupName。チェックを変えても名前が変わらないように)。
    // 並びは、まとまりの名前と 1 か所の行の名前で名前の順。開いているまとまりの中の行は、今の表示を一段下げて名前の順に出す
    private List<Row> GroupRows(List<Row> items, List<ShopTreeNode> places)
    {
        var groupKeys = items.Where(r => r.Group != null).GroupBy(r => r.Group!).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        // 見えるようにする場所がまとまりの中なら、そのまとまりを開く (1 回だけ。後で閉じられるように)
        if (_reveal != null && items.FirstOrDefault(r => r.Record == _reveal) is { Group: { } rk } && groupKeys.Contains(rk)) { _openGroups.Add(rk); _reveal = null; }
        var names = GroupNames(places);
        var top = new List<(string Sort, List<Row> Rows)>();
        foreach (var r in items.Where(r => r.Group == null || !groupKeys.Contains(r.Group))) top.Add((r.Label, new List<Row> { r with { Group = null } }));
        foreach (var g in items.Where(r => r.Group != null && groupKeys.Contains(r.Group)).GroupBy(r => r.Group!))
        {
            var name = names.TryGetValue(g.Key, out var gnm) ? gnm : GroupName(places.Where(n => _keys!.TryGetValue(n.RecordName, out var k) && k == g.Key).Select(n => n.Name)
                .Concat(s_unlisted.Where(u => _keys!.TryGetValue(u.Record, out var k) && k == g.Key).Select(u => u.Name)));
            var open = _openGroups.Contains(g.Key);
            // 閉じているときは ▼ (押すと開く)、開いているときは ▲ (押すと閉じる)。船の 3D の一覧 (wwwroot/ship3d/viewer.html) と同じ
            var rows = new List<Row> { new("", name, name, $"{(open ? "▲" : "▼")} {name}（{g.Count()} か所）", g.Key, IsGroup: true) };
            if (open) rows.AddRange(g.OrderBy(r => r.Label, StringComparer.OrdinalIgnoreCase).Select(r => r with { Display = "　　" + r.Display, InGroup = true }));
            top.Add((name, rows));
        }
        return top.OrderBy(t => t.Sort, StringComparer.OrdinalIgnoreCase).SelectMany(t => t.Rows).ToList();
    }

    // その星系の、同じ地図のまとまり (チェック・絞り込みの前に 2 か所以上あるもの) の鍵 → まとまりの名前。
    // 名前 (GroupName) が同じになるまとまりどうしは、まとまりの場所の親の場所のゲームの名前 (HasGameName。多い順、同じ数なら名前の順) を
    // 3 つまでなら「・」でつないで、4 つ以上なら 2 つと「ほか」を括弧で添える。それでも同じになるまとまりは、そのまま
    private Dictionary<string, string> GroupNames(List<ShopTreeNode> places)
    {
        var res = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_keys == null) return res;
        var byKey = places.Where(n => _keys.ContainsKey(n.RecordName)).GroupBy(n => _keys[n.RecordName]).Where(g => g.Count() > 1).ToList();
        foreach (var g in byKey) res[g.Key] = GroupName(g.Select(n => n.Name));
        foreach (var same in byKey.GroupBy(g => res[g.Key]).Where(x => x.Count() > 1))
            foreach (var g in same)
            {
                var parents = g.Select(n => n.Parent).Where(p => p != null && HasGameName(p)).GroupBy(p => p!.Name)
                    .OrderByDescending(x => x.Count()).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => x.Key).ToList();
                if (parents.Count == 0) continue;
                res[g.Key] = $"{res[g.Key]}（{(parents.Count <= 3 ? string.Join("・", parents) : string.Join("・", parents.Take(2)) + " ほか")}）";
            }
        return res;
    }

    private void LstSystems_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_suppress) RefreshStations(); }
    private void TxtFilter_TextChanged(object sender, TextChangedEventArgs e) { if (!_suppress) RefreshStations(); }

    private async void LstStations_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || lstStations.SelectedItem is not Row row) return;
        if (row.IsGroup)
        {
            // まとまりの行: 押すと開く・閉じる。選んでいる場所はそのまま (作り直した一覧で選び直す)
            if (!_openGroups.Remove(row.Group!)) _openGroups.Add(row.Group!);
            RefreshStations();
            return;
        }
        await SelectAsync(row.Record, row.Name);
    }

    private async Task SelectAsync(string record, string name)
    {
        _record = record;
        _name = name;
        Title = $"ステーションの地図 - {name}";
        UpdateBar();
        await panel.ShowAsync(record, name, _tree);
    }

    private string _name = "";

    // ［読み込み］の有効・無効と、その場所の状態の文 (［詳細］のバーと同じ決まり)
    private void UpdateBar()
    {
        if (_record == null) { btnImport.IsEnabled = false; txtStatus.Text = ""; return; }
        var ex = StationMapExports.Instance;
        var state = ex.StateOf(_record);
        var key = StationMapExports.KeyOf(StationMapExports.ShortName(_record));
        var job = Ship3DExports.Instance.Jobs.FirstOrDefault(j => j.Short.Equals(key, StringComparison.OrdinalIgnoreCase));
        btnImport.IsEnabled = state == Ship3DCacheState.None && job == null && !Ship3DExports.Instance.CpuHigh;
        txtStatus.Text = job != null ? $"取り込み中: {job.Text}"
            : state switch
            {
                Ship3DCacheState.Fresh => "取り込み済み",
                Ship3DCacheState.NeedsVerify or Ship3DCacheState.Verifying => "照合中 (パッチで Data.p4k が変わったので元データを確かめています)",
                Ship3DCacheState.NoGame => "ゲームの Data.p4k が見つかりません",
                _ => Ship3DExports.Instance.CpuHigh ? $"未取り込み。CPU 使用率 {Ship3DExports.Instance.CpuPercent:F0}% のため取り込みを始められません (90% 未満になったら使えます)"
                                                    : "未取り込み (［読み込み］でゲームデータから取り込みます)",
            };
    }

    // ［読み込み］: 予想を出して確かめてから裏で取り込む。終わったら (この窓がまだその場所なら、パネルが Exported で) 出す
    private async void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        var record = _record;
        if (record == null) return;
        btnImport.IsEnabled = false;
        await StationMapExports.Instance.ImportAsync(record, _name, this);
        RefreshStations();
        UpdateBar();
    }
}
