using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

// ［船舶管理］の［電力管理］。船を選び、電力に関わる部品 (パワープラント・クーラー・シールド・レーダー・生命維持・量子ドライブ・武器) に
// ピップを割り振ったときの結果を出す。計算は PowerMathShip.Compute (GameDataQueryService.Power.cs)。
//   所持船: ［装備］の船セレクタと同じ一覧 (OwnedShipsProvider)。最初の部品は［装備］の現在の装備 (HangarService.GetShipLoadout)。
//   全船: GameDataQueryService.GetDetailShipList。最初の部品は既定の装備 (ship_ports)。
//   部品の候補: 所持コンポーネント (［装備］と同じ IsLoadoutCandidate) か全部品 (EquipmentService.GetShipComponents を同じ条件で絞る)。
//   ここで替えた部品・割り当て・品質は保存しない (ship_loadouts にも書かない)。画面に来るたび (表示されたとき) に読み直す。
// 読み込み (船の一覧・部品の行・部品の候補の電力のデータ・品質の窓の訳) は裏のスレッドで行い、終わってから画面に入れる (BlueprintPanel と同じ作り)。
// 新しい読み込みが始まったら、前の読み込みは途中でやめ (CancellationToken)、結果も捨てる。
// DB はこの画面だけの口 (_query・_equip・_bgHangar。［装備］の口とは別) で、裏のスレッドだけが _dbLock の中で使う。
// UI のスレッドは DB もロックも使わない (候補を選んだときは、読み込みのときに裏で読んでおいた値 (PowerCandidate.Item など) を使う)
public partial class PowerPanel : UserControl
{
    public Func<string>? WorkDirProvider { get; set; }
    public Func<IEnumerable<LoadoutShipChoice>>? OwnedShipsProvider { get; set; }
    public Func<IReadOnlyList<MyComponent>>? MyComponentsProvider { get; set; }

    // 電力管理で行にする部品の種類 (items.item_type)
    private static readonly HashSet<string> PowerTypes = new(StringComparer.OrdinalIgnoreCase)
        { "PowerPlant", "Cooler", "Shield", "Radar", "LifeSupportGenerator", "QuantumDrive", "WeaponGun" };

    private readonly object _dbLock = new();
    private GameDataQueryService? _query;   // _dbLock の中で使う
    private EquipmentService? _equip;       // 同上 (読むだけの口 EquipmentService.OpenReadOnly)
    private string? _dbPath;
    private HangarService? _bgHangar;       // 同上 (船の record の解決と［装備］の現在の装備を読む。MainWindow の HangarService の覚えと別にするため、この画面だけのもの)
    private QualityNames? _names;           // 品質の窓の枠・性質の訳 (全部の BP の分。訳が作り直されたら読み直す)
    private string? _namesStamp;            // _names を読んだときの translations.db (パスと更新日時)
    private List<PowerRow> _rows = new();
    private PowerRow? _poolRow;
    private PowerShipPools _pools = new(false, null, false, null);
    private bool _suppress;
    private string? _loadedKey;         // 今の行を作った船 (PowerShipChoice.Key)
    private string? _loadedSignature;   // 今の行を作ったときの［装備］の現在の装備と所持コンポーネントの印 (Signature)
    private int _loadVersion;           // 読み込みの番号 (古い読み込みの結果を捨てる)
    private CancellationTokenSource? _loadCts;   // 今の読み込みをやめさせる
    private bool _loading;              // 読み込みが終わっていない (次の読み込みが前の読み込みの頼みを引き継ぐ)
    private bool _pendingReloadList, _pendingPreserve, _pendingOnlyIfChanged;   // 終わっていない読み込みの頼み
    private string _shownStatus = "";   // 最後に終わった読み込みの結果の状態の文 (変わっていなかったときはこれを出す)
    private bool _dirty;                // インデックスの作り直し・取り込みなどで DB・訳が替わった (次の読み込みで口を開き直す)
    private bool _qualityOpening;       // 品質の窓を開くための訳の読み込みの最中

    // 今の読み込み (確かめ用)
    internal Task LoadTask { get; private set; } = Task.CompletedTask;

    public PowerPanel()
    {
        InitializeComponent();
        // 画面に来たときは、船か［装備］の現在の装備 (所持コンポーネントを含む) が変わったときだけ読み直す (動かした割り当てを残す)
        // 画面を離れたら、今の読み込み (使用中の待ちも) をやめる。頼みは残し、次に来たときの読み込みが引き継ぐ
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) RefreshIfChanged(); else _loadCts?.Cancel(); };
    }

    // 状態の欄の読み込み中の文
    private const string LoadingStatus = "読み込み中...";
    // 使用中・時間がかかっているときの文は GameDataQueryService.BusyWaitStatus / SlowWaitStatus (DB の名前を入れる。分からなければ「DB」)。
    // 名前は、読み込みごとの記録 (SqliteReadContext) から出す: 使用中の例外が出た接続の DB・最後に読み始めた DB
    private const int BusyRetryMs = 2000;    // 使用中だったときに読み直すまでの間
    private const int SlowNoticeMs = 3000;   // この時間たっても終わらなければ GameDataQueryService.SlowWaitStatus を出す

    // DB が使用中 (SQLITE_BUSY = 5 / SQLITE_LOCKED = 6) で読めなかったか
    internal static bool IsBusy(Exception? ex) => GameDataQueryService.IsBusy(ex);

    // HangarService.ResolveShipRecordName は例外を中で受けて OnProgress に「…: {ex.Message}」を出すので、その文で使用中を見分ける
    private static bool IsBusyMessage(string? message) =>
        message != null && (message.Contains("SQLite Error 5:") || message.Contains("SQLite Error 6:"));

    private string WorkDir => WorkDirProvider?.Invoke() ?? "";
    private string GamedataPath => Path.Combine(WorkDir, "gamedata_cache.db");
    private string TranslationsPath => Path.Combine(WorkDir, "translations.db");

    // インデックスの作り直し・装備データの再抽出・バックアップの取り込み・作業ディレクトリの変更・訳の変更の後に MainWindow から呼ぶ
    // (BlueprintPanel.Reload と同じ所)。DB の口と訳を開き直す。表示していなければ印だけ付け、次に表示したときに読む。別のスレッドからは UI のスレッドへ回す
    public void Reload()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Reload);
            return;
        }
        _dirty = true;
        if (IsVisible) StartLoad(reloadList: true, preserve: true, onlyIfChanged: false);
    }

    // ［装備］の所持コンポーネントの表の読み込みが終わった (MyComponentsProvider の一覧が替わった) ときに MainWindow から呼ぶ。
    // 表示中なら、その場で読み直す (画面に来たときと同じく、同じ船で現在の装備・所持コンポーネントの印が前と同じなら何もしない)。
    // 表示していなければ何もしない (次に来たときの読み直しが、新しい一覧で印を比べる)。別のスレッドからは UI のスレッドへ回す
    public void MyComponentsChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(MyComponentsChanged);
            return;
        }
        if (IsVisible) StartLoad(reloadList: false, preserve: true, onlyIfChanged: !_dirty);
    }

    // DB の口を開く (裏のスレッドで、_dbLock の中で呼ぶ)。同じファイルなら前の口のまま
    private (GameDataQueryService Query, EquipmentService Equip) OpenDbs(string path)
    {
        if (_query != null && _equip != null && string.Equals(_dbPath, path, StringComparison.OrdinalIgnoreCase)) return (_query, _equip);
        CloseDbs();
        var q = new GameDataQueryService(path);
        try
        {
            var e = EquipmentService.OpenReadOnly(path);
            _query = q;
            _equip = e;
            _dbPath = path;
            return (q, e);
        }
        catch
        {
            q.Dispose();
            throw;
        }
    }

    // DB の口を閉じる (裏のスレッドで、_dbLock の中で呼ぶ)。船の record の解決の覚え (_bgHangar) も捨てる
    private void CloseDbs()
    {
        try { _query?.Dispose(); } catch { }
        try { _equip?.Dispose(); } catch { }
        _query = null;
        _equip = null;
        _dbPath = null;
        _bgHangar = null;
    }

    // この画面だけの HangarService (裏のスレッドで、_dbLock の中で呼ぶ)
    private HangarService BgHangar(string workDir)
    {
        _bgHangar ??= new HangarService();
        _bgHangar.SetCacheDir(workDir);
        return _bgHangar;
    }

    // translations.db の印 (パス・更新日時・長さ。WAL の -wal も見る: SqliteWal.FileStamp)。訳が作り直されたら変わる
    private static string NamesStampOf(string translationsPath) => SqliteWal.FileStamp(translationsPath);

    // 画面に来たとき: 船の一覧を作り直し、選んでいる船が前と違えば行を作り直す。同じ船なら、［装備］の現在の装備・所持コンポーネントが
    // 前と違うときだけ作り直す (部品・割り当て・品質は、同じ搭載枠で同じ部品のままの行だけ残す)。どちらも同じなら何もしない。
    // DB が替わった (Reload) あとは必ず作り直す
    private void RefreshIfChanged() => StartLoad(reloadList: true, preserve: true, onlyIfChanged: !_dirty);

    private void ShipSource_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        StartLoad(reloadList: true, preserve: false, onlyIfChanged: false);
    }

    private void Ship_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        StartLoad(reloadList: false, preserve: false, onlyIfChanged: false);
    }

    // 部品の候補 (所持コンポーネント / 全部品) を替えたときは、候補だけ作り直し、部品・割り当てはできるだけ残す
    private void PartSource_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        StartLoad(reloadList: false, preserve: true, onlyIfChanged: false);
    }

    // ［読み直す］: 船の一覧・［装備］の現在の装備・所持コンポーネントを読み直し、割り当てを全部最大に戻す
    private void Reload_Click(object sender, RoutedEventArgs e) => StartLoad(reloadList: true, preserve: false, onlyIfChanged: false);

    // 品質の比べる鍵 (揃えた JSON。品質なしは "")
    private static string QualityKey(IReadOnlyDictionary<string, int>? q) => q == null || q.Count == 0 ? "" : PowerMath.ToQualityJson(q);

    // 読み込みを始める (前の読み込みは途中でやめ、結果も捨てる)。
    //   reloadList: 船の一覧も作り直す (全船の一覧は裏のスレッドで読む)
    //   preserve: 同じ船なら、同じ搭載枠で同じ部品のままの行の部品・割り当て・オン/オフ・全部品で入れた品質と、武器のプールの割り当てを残す
    //   onlyIfChanged: 同じ船で［装備］の現在の装備と所持コンポーネントの印が前と同じなら何もしない
    // 前の読み込みが終わる前に呼ばれたら、前の頼みを引き継ぐ (一覧の作り直しは足し、残す・変わっていなければ何もしない は両方が頼んだときだけ)。
    // やめた読み込みの頼み (たとえば［読み直す］の全部最大から) が、あとの「変わっていなければ何もしない」で消えないように
    private Task StartLoad(bool reloadList, bool preserve, bool onlyIfChanged)
    {
        if (_loading)
        {
            reloadList |= _pendingReloadList;
            preserve &= _pendingPreserve;
            onlyIfChanged &= _pendingOnlyIfChanged;
        }
        _loading = true;
        (_pendingReloadList, _pendingPreserve, _pendingOnlyIfChanged) = (reloadList, preserve, onlyIfChanged);
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var version = ++_loadVersion;
        var task = LoadCoreAsync(version, reloadList, preserve, onlyIfChanged, new SqliteReadContext(), _loadCts.Token);
        LoadTask = task;
        return task;
    }

    // ctx: この読み込みの DB の記録 (使用中・時間がかかっているときの文の DB の名前)
    private async Task LoadCoreAsync(int version, bool reloadList, bool preserve, bool onlyIfChanged, SqliteReadContext ctx, CancellationToken token)
    {
        txtStatus.Text = LoadingStatus;
        _ = SlowNoticeAsync(version, ctx, token);
        var reopen = _dirty;
        try
        {
            var done = await LoadStepsAsync(version, reloadList, preserve, onlyIfChanged, reopen, ctx, token);
            if (!done || version != _loadVersion) return;
            // 最後に頼んだ読み込みが終わった
            _loading = false;
            if (reopen) _dirty = false;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }   // 新しい読み込みが始まったのでやめた
    }

    // 読み込みが SlowNoticeMs たっても終わらなければ、待っている訳を出す (DB の口をもう開いてあって、中で使用中を待っているときなど)
    // (その読み込みが最後に読み始めた DB (ctx) が分かればその名前)
    private async Task SlowNoticeAsync(int version, SqliteReadContext ctx, CancellationToken token)
    {
        try { await Task.Delay(SlowNoticeMs, token); }
        catch (OperationCanceledException) { return; }
        if (version == _loadVersion && txtStatus.Text == LoadingStatus)
            txtStatus.Text = GameDataQueryService.SlowWaitStatus(SqliteReadContext.FileNameOf(ctx.CurrentPath));
    }

    // 使用中だったとき: 訳を出し、間を置く (船を替えた・画面を離れたら token でやめる)。dbName は使用中の例外が出た接続の DB (分からなければ null)
    private async Task WaitBusyAsync(string? dbName, CancellationToken token)
    {
        txtStatus.Text = GameDataQueryService.BusyWaitStatus(dbName);
        await Task.Delay(BusyRetryMs, token);
    }

    // 読み込みの本体。最後まで済んだら true (新しい読み込みに替わったら false)。状態の文は、この読み込みの結果だけから決める
    private async Task<bool> LoadStepsAsync(int version, bool reloadList, bool preserve, bool onlyIfChanged, bool reopen, SqliteReadContext ctx, CancellationToken token)
    {
        var workDir = WorkDir;
        var dbPath = GamedataPath;
        if (reloadList)
        {
            var prev = (cmbShip.SelectedItem as PowerShipChoice)?.Key;
            var list = new List<PowerShipChoice>();
            string? error = null;
            if (rbOwnedShips.IsChecked == true)
            {
                try
                {
                    foreach (var s in OwnedShipsProvider?.Invoke() ?? [])
                        list.Add(new PowerShipChoice { Key = "my:" + s.ShipKey, Display = s.Display, Owned = s });
                }
                catch (Exception ex) { error = $"船の一覧を読めません: {ex.Message}"; }
            }
            else
            {
                // 使用中なら、間を置いて読み直す
                while (true)
                {
                    var doReopen = reopen;
                    var busy = false;
                    string? busyName = null;
                    try { list = await Task.Run(() => { ctx.ClearBusy(); return ctx.Run(() => ReadAllShips(dbPath, doReopen, token)); }, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
                    catch (Exception ex) when (IsBusy(ex)) { busy = true; busyName = ctx.BusyNameOf(ex); }
                    catch (Exception ex) { error = $"船の一覧を読めません: {ex.Message}"; }
                    reopen = false;
                    if (version != _loadVersion) return false;
                    if (!busy) break;
                    await WaitBusyAsync(busyName, token);
                }
            }
            if (version != _loadVersion) return false;
            _suppress = true;
            try
            {
                cmbShip.ItemsSource = list;
                cmbShip.SelectedItem = list.FirstOrDefault(s => s.Key == prev) ?? (list.Count > 0 ? list[0] : null);
            }
            finally { _suppress = false; }
            if (error != null) { ClearRows(error); return true; }
        }

        if (cmbShip.SelectedItem is not PowerShipChoice ship) { ClearRows("船を選択してください"); return true; }
        var sameShip = ship.Key == _loadedKey;
        var input = new LoadInput
        {
            WorkDir = workDir,
            DbPath = dbPath,
            TranslationsPath = Path.Combine(workDir, "translations.db"),
            Reopen = reopen,
            NamesStamp = reopen ? null : _namesStamp,
            Record = ship.Owned != null ? ship.Owned.Record : ship.Record,
            ShipName = ship.Owned?.Name,
            Manufacturer = ship.Owned?.Manufacturer,
            ShipKey = ship.Owned?.ShipKey ?? "",   // 全船: ship_loadouts の上書きの無いキー (既定の装備)
            Owned = ship.Owned != null,
            Owns = rbOwnedParts.IsChecked == true,
            Mine = (MyComponentsProvider?.Invoke() ?? []).ToList(),
            OnlyIfChanged = onlyIfChanged && sameShip,
            PrevSignature = _loadedSignature,
            Token = token,
        };
        LoadResult result;
        // 使用中なら、エラーで止めずに間を置いて読み直す
        while (true)
        {
            string? busyName = null;
            try { result = await Task.Run(() => { ctx.ClearBusy(); return ctx.Run(() => LoadData(input)); }, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
            catch (Exception ex) when (IsBusy(ex)) { result = new LoadResult { Busy = true }; busyName = ctx.BusyNameOf(ex); }
            catch (Exception ex) { result = new LoadResult { Message = $"読み込みエラー: {ex.Message}" }; }
            if (version != _loadVersion) return false;
            if (!result.Busy) break;
            // 船の record の解決が中で使用中を受けたとき (例外にならない) は、この読み込みで最後に使用中で読めなかった DB
            busyName ??= ctx.BusyNameOf(null);
            input.Reopen = false;   // 口の開き直しは最初の 1 回で済んでいる
            await WaitBusyAsync(busyName, token);
        }

        // 所持船の record は［装備］の船セレクタと同じく、解決したものを覚える (UI のスレッドで書く)
        if (ship.Owned is { } owned && owned.Record == null && result.Record != null) owned.Record = result.Record;
        if (result.Names != null) { _names = result.Names; _namesStamp = result.NamesStamp; }
        if (result.Unchanged) { txtStatus.Text = _shownStatus; return true; }
        if (result.Message != null) { ClearRows(result.Message.Replace("{ship}", ship.Display)); return true; }
        Apply(result, ship, preserve && sameShip);
        return true;
    }

    // 行を消して知らせを出す
    private void ClearRows(string message)
    {
        _rows = new List<PowerRow>();
        _poolRow = null;
        _loadedKey = null;
        _loadedSignature = null;
        dgParts.ItemsSource = null;
        dgWeapons.ItemsSource = null;
        txtStatus.Text = _shownStatus = message;
        ShowSummary(null);
    }

    // 全船の一覧 (裏のスレッド)
    private List<PowerShipChoice> ReadAllShips(string dbPath, bool reopen, CancellationToken token)
    {
        lock (_dbLock)
        {
            token.ThrowIfCancellationRequested();
            if (reopen) CloseDbs();
            if (!File.Exists(dbPath)) return new List<PowerShipChoice>();
            var (q, _) = OpenDbs(dbPath);
            return q.GetDetailShipList().OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(s => new PowerShipChoice { Key = "all:" + s.Record, Display = s.Name, Record = s.Record }).ToList();
        }
    }

    // ［装備］の現在の装備 (搭載枠ごとの今の部品) と所持コンポーネント (id・部品・使用可・数量・品質) の印
    private static string Signature(IEnumerable<ShipPortLoadout> ports, IEnumerable<MyComponent> mine) =>
        string.Join("\n", ports.Select(p => p.PortKey + "=" + p.EffectiveItemRecord)) + "\n--\n"
        + string.Join("\n", mine.Select(c => $"{c.Id}|{c.ItemRecord}|{c.Usable}|{c.Quantity}|{c.QualityJson}"));

    private sealed class LoadInput
    {
        public string WorkDir = "";
        public string DbPath = "";
        public string TranslationsPath = "";
        public bool Reopen;
        public string? NamesStamp;
        public string? Record;           // ships.record_name (所持船でまだ解決していなければ null)
        public string? ShipName, Manufacturer;   // 所持船の record を解決する名前
        public string ShipKey = "";
        public CancellationToken Token;
        public bool Owned;
        public bool Owns;
        public List<MyComponent> Mine = new();
        public bool OnlyIfChanged;
        public string? PrevSignature;
    }

    private sealed class LoadResult
    {
        public string? Message;          // 行を作れなかったときの知らせ ({ship} は船の表示名に替える)
        public string? Record;           // 解決した船の record
        public bool Busy;                // DB が使用中で読めなかった (間を置いて読み直す)
        public bool Unchanged;           // 印が前と同じ (何もしない)
        public QualityNames? Names;      // 読み直した訳 (読み直さなければ null)
        public string? NamesStamp;
        public List<PowerRow> Rows = new();         // 武器でない行 (パワープラントを先に、ほかは搭載枠の順)
        public List<PowerRow> WeaponRows = new();
        public PowerShipPools Pools = new(false, null, false, null);
        public string Signature = "";
    }

    // 選んだ船の部品の行を作る (裏のスレッド)。部品は最初の部品 (現在の装備) で、割り当ては全部最大
    private LoadResult LoadData(LoadInput input)
    {
        lock (_dbLock)
        {
            if (input.Reopen) CloseDbs();
            if (!File.Exists(input.DbPath)) return new LoadResult { Message = "gamedata_cache.db がありません" };
            GameDataQueryService q;
            EquipmentService equip;
            try { (q, equip) = OpenDbs(input.DbPath); }
            catch (Exception ex) when (IsBusy(ex)) { return new LoadResult { Busy = true }; }
            catch (Exception ex) { return new LoadResult { Message = $"gamedata_cache.db を開けません: {ex.Message}" }; }

            var token = input.Token;
            token.ThrowIfCancellationRequested();
            var res = new LoadResult();
            var hangar = BgHangar(input.WorkDir);
            var record = input.Record;
            string? resolveError = null;
            if (record == null && input.ShipName != null)
            {
                // 解決の失敗の知らせ (HangarService.ResolveShipRecordName の「装備解決に失敗しました (…): {ex.Message}」) だけを見る
                void OnResolveProgress(string m) { if (m.StartsWith("装備解決に失敗しました", StringComparison.Ordinal)) resolveError = m; }
                hangar.OnProgress += OnResolveProgress;
                try { record = hangar.ResolveShipRecordName(input.ShipName, input.Manufacturer); }
                finally { hangar.OnProgress -= OnResolveProgress; }
            }
            if (record == null)
            {
                // ResolveShipRecordName は見つからなかった null も覚えるので、覚えた HangarService ごと捨てる (次の読み込みで解決し直す)
                _bgHangar = null;
                if (IsBusyMessage(resolveError)) return new LoadResult { Busy = true };
                return new LoadResult { Message = "「{ship}」の装備データ (ships.record_name) を解決できません" + (resolveError != null ? $" ({resolveError})" : "") };
            }
            res.Record = record;
            var stamp = NamesStampOf(input.TranslationsPath);
            if (input.NamesStamp != stamp)
            {
                res.Names = QualityNames.LoadAll(input.DbPath, input.TranslationsPath);
                res.NamesStamp = stamp;
            }
            token.ThrowIfCancellationRequested();
            var ports = hangar.GetShipLoadout(input.ShipKey, record, equip, throwIfBusy: true);   // 使用中は待って読み直す
            res.Signature = Signature(ports, input.Mine);
            if (input.OnlyIfChanged && res.Signature == input.PrevSignature) { res.Unchanged = true; return res; }

            res.Pools = q.GetShipPowerPools(record);
            var mine = input.Mine;
            // 部品の電力のデータ・BP の品質・武器の種別の名前を、record ごとに一度だけ読む (候補を選んだときに UI のスレッドで DB を読まないように)
            var loaded = new Dictionary<string, (PowerItemData? Item, BlueprintQualityInfo? Quality, string WeaponKind)>(StringComparer.OrdinalIgnoreCase);
            void Fill(PowerCandidate c)
            {
                token.ThrowIfCancellationRequested();
                const string prefix = "EntityClassDefinition.";
                var key = c.Record.Trim();
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) key = key[prefix.Length..];
                if (!loaded.TryGetValue(key, out var v))
                {
                    var item = q.GetPowerItem(c.Record);
                    v = (item, item != null ? q.GetBlueprintQuality(item.Record) : null,
                        item?.Weapon != null ? equip.WeaponDamageKindLabel(item.Record) : "");
                    loaded[key] = v;
                }
                (c.Item, c.Quality, c.WeaponKind) = v;
            }
            foreach (var p in ports)
            {
                token.ThrowIfCancellationRequested();
                var rec = p.EffectiveItemRecord;
                if (string.IsNullOrEmpty(rec)) continue;
                var data = q.GetPowerItem(rec);
                if (data == null || !data.HasResourceComponent || !PowerTypes.Contains(data.ItemType)) continue;
                var row = new PowerRow
                {
                    Panel = this,
                    Port = p,
                    PortDisplay = p.PortDisplay.Length > 0 ? p.PortDisplay : p.PortName,
                    TypeDisplay = p.WeaponKindDisplay.Length > 0 ? p.WeaponKindDisplay : p.TypeDisplay,   // ［装備］の表と同じ種別の名前
                    ItemType = data.ItemType,
                    AllowQualityInput = !input.Owns,
                };
                // 最初の部品 (現在の装備)。ship_loadouts は item_record だけを持つので、品質は所持コンポーネントの同じ item_record の行から決める:
                // 行の品質が 1 通りだけ (行が 1 つ、または全部同じ品質) ならその品質。品質の違う行が複数あれば決められないので品質なしとし、注に出す
                // (部品の候補で選び直せる)。全船 (既定の装備) は所持していないので品質なし
                Dictionary<string, int>? currentQualities = null;
                string? currentNote = null;
                if (input.Owned)
                {
                    var same = mine.Where(c => HangarService.SameRecord(c.ItemRecord, data.Record)).ToList();
                    var kinds = same.Select(c => QualityKey(PowerMath.ParseQualityJson(c.QualityJson))).Distinct().ToList();
                    if (kinds.Count == 1) currentQualities = PowerMath.ParseQualityJson(same[0].QualityJson);
                    else if (kinds.Count > 1)
                        currentNote = $"所持コンポーネントに品質の違う同じ部品が {same.Count} 行あり、どれか決められない (品質なしとして計算。部品の候補で選び直せる)";
                }
                var current = new PowerCandidate
                {
                    Record = data.Record,
                    Display = $"{ItemLabel(equip, data.Record)} (現在の装備)" + (PowerMath.QualityLabel(currentQualities) is { Length: > 0 } cq ? " " + cq : ""),
                    Qualities = currentQualities,
                    IsCurrent = true,
                };
                row.CurrentNote = currentNote;
                row.Candidates.Add(current);
                foreach (var c in BuildCandidates(p, record, data.ItemType, input.Owns, mine, equip))
                {
                    // 現在の装備と同じ部品・同じ品質の候補は 1 つにする (品質を決められなかったときは、選び直せるように全部残す)
                    if (currentNote == null && HangarService.SameRecord(c.Record, current.Record) && QualityKey(c.Qualities) == QualityKey(current.Qualities)) continue;
                    row.Candidates.Add(c);
                }
                foreach (var c in row.Candidates) Fill(c);
                row.SetCandidate(current, rebuildPips: true);
                if (data.Weapon != null) res.WeaponRows.Add(row);
                else res.Rows.Add(row);
            }
            // パワープラントを先に、ほかは搭載枠の順
            res.Rows = res.Rows.Where(r => r.ItemType.Equals("PowerPlant", StringComparison.OrdinalIgnoreCase))
                .Concat(res.Rows.Where(r => !r.ItemType.Equals("PowerPlant", StringComparison.OrdinalIgnoreCase))).ToList();
            return res;
        }
    }

    // 読んだ行を画面に入れる (UI のスレッド)。preserve なら、同じ搭載枠で同じ部品のままの行だけ、前の部品・割り当て・オン/オフ・全部品で入れた品質を残す:
    //   ［装備］の現在の装備 (最初の部品) が前と替わった搭載枠は、新しい現在の装備を選び、割り当ては最大から (前の部品は残さない)。
    //   現在の装備が前と同じなら、前に選んでいた候補 (現在の装備か、同じ部品 (record と品質) の候補) を選び直し、割り当てを戻す。無ければ現在の装備・最大から
    private void Apply(LoadResult res, PowerShipChoice ship, bool preserve)
    {
        var old = preserve
            ? _rows.Where(r => !r.IsPool && r.State != null && r.SelectedCandidate != null)
                .GroupBy(r => r.Port?.PortKey ?? "").ToDictionary(g => g.Key, g => g.First())
            : new Dictionary<string, PowerRow>();
        int? oldPool = preserve ? _poolRow?.Pips : null;
        _pools = res.Pools;
        foreach (var row in res.Rows.Concat(res.WeaponRows))
            if (old.TryGetValue(row.Port?.PortKey ?? "", out var prev)) RestoreFrom(row, prev);

        var rows = new List<PowerRow>(res.Rows);
        _poolRow = null;
        // 武器のプールの行と武器は最後
        if (res.WeaponRows.Count > 0 && _pools.WeaponPoolSize is int poolSize && poolSize > 0)
        {
            _poolRow = new PowerRow { Panel = this, IsPool = true, PortDisplay = "武器のプール", TypeDisplay = "武器", ItemType = "WeaponPool", PoolSize = poolSize };
            _poolRow.Pips = Math.Clamp(oldPool ?? poolSize, 0, poolSize);
            rows.Add(_poolRow);
        }
        rows.AddRange(res.WeaponRows);
        _rows = rows;
        dgParts.ItemsSource = _rows;
        txtStatus.Text = _shownStatus = $"{ship.Display}: 電力に関わる部品 {_rows.Count(r => !r.IsPool)} 行" + (_pools.Found ? "" : " (船の電力プールのデータが無い)");
        _loadedKey = ship.Key;
        _loadedSignature = res.Signature;
        Recompute();
    }

    private void RestoreFrom(PowerRow row, PowerRow prev)
    {
        var newCurrent = row.Candidates.FirstOrDefault(c => c.IsCurrent);
        var prevCurrent = prev.Candidates.FirstOrDefault(c => c.IsCurrent);
        if (newCurrent == null || prevCurrent == null) return;
        // 現在の装備が替わった (［装備］で部品を替えた): 新しい現在の装備・最大のまま
        if (!HangarService.SameRecord(newCurrent.Record, prevCurrent.Record) || QualityKey(newCurrent.Qualities) != QualityKey(prevCurrent.Qualities)) return;
        if (prev.SelectedCandidate is not { } pc || prev.State is not { } ps) return;
        var match = pc.IsCurrent
            ? newCurrent
            : row.Candidates.FirstOrDefault(c => !c.IsCurrent && HangarService.SameRecord(c.Record, pc.Record) && QualityKey(c.Qualities) == QualityKey(pc.Qualities));
        if (match == null) return;   // 前の部品が候補に無い: 現在の装備・最大から
        if (!ReferenceEquals(match, row.SelectedCandidate)) SetRowCandidate(row, match, recompute: false);
        if (row.State == null) return;
        row.State.Pips = ps.Pips;
        row.State.On = ps.On;
        if (!match.FromOwned && !match.IsCurrent && ps.Qualities != null) row.State.Qualities = ps.Qualities;
    }

    // 行の部品を替える (UI のスレッド。割り当ては最大から)。値は読み込みのときに裏で読んでおいたもの (DB もロックも使わない)
    internal void SetRowCandidate(PowerRow row, PowerCandidate c, bool recompute = true)
    {
        row.SetCandidate(c, rebuildPips: true);
        if (recompute) Recompute();
    }

    // 部品の候補。owns なら所持コンポーネント (［装備］と同じ IsLoadoutCandidate)、そうでなければ全部品 (GetShipComponents を同じ条件で絞る)。
    // 武器の行は武器 (WeaponGun) だけ (ジンバルの付け替えはここではしない)。種類が同じ部品だけ
    private static IEnumerable<PowerCandidate> BuildCandidates(ShipPortLoadout p, string shipRecord, string itemType, bool owns,
        IReadOnlyList<MyComponent> mine, EquipmentService equip)
    {
        if (owns)
        {
            foreach (var c in mine)
            {
                if (!c.ItemType.Equals(itemType, StringComparison.OrdinalIgnoreCase)) continue;
                if (!HangarService.IsLoadoutCandidate(p, c, equip, shipRecord)) continue;
                var qualities = PowerMath.ParseQualityJson(c.QualityJson);
                var parts = new List<string>();
                if (c.SizeDisplay.Length > 0) parts.Add(c.SizeDisplay);
                if (c.GradeDisplay.Length > 0) parts.Add(c.GradeDisplay);
                parts.Add(HangarService.MyComponentDisplayName(c, equip));
                parts.Add($"×{c.Quantity}");
                if (PowerMath.QualityLabel(qualities) is { Length: > 0 } ql) parts.Add(ql);
                yield return new PowerCandidate { Record = c.ItemRecord, Display = string.Join(" ", parts), Qualities = qualities, FromOwned = true };
            }
            yield break;
        }
        foreach (var it in equip.GetShipComponents(itemType))
        {
            if (!it.ItemType.Equals(itemType, StringComparison.OrdinalIgnoreCase)) continue;
            var probe = new MyComponent { ItemRecord = it.RecordName, ItemType = it.ItemType, Size = it.Size, Grade = it.Grade, Usable = true };
            if (!HangarService.IsLoadoutCandidate(p, probe, equip, shipRecord)) continue;
            var parts = new List<string>();
            if (it.Size > 0) parts.Add($"S{it.Size}");
            if (MyComponent.GradeToClass(it.Grade) is { Length: > 0 } g) parts.Add(g);
            parts.Add(it.Name);
            yield return new PowerCandidate { Record = it.RecordName, Display = string.Join(" ", parts) };
        }
    }

    private static string ItemLabel(EquipmentService equip, string record)
    {
        var it = equip.GetItemByRecord(record);
        var name = equip.GetCurrentItemName(record) ?? it?.Name ?? record;
        var parts = new List<string>();
        if (it != null && it.Size > 0) parts.Add($"S{it.Size}");
        if (it != null && MyComponent.GradeToClass(it.Grade) is { Length: > 0 } g) parts.Add(g);
        parts.Add(name);
        return string.Join(" ", parts);
    }

    private void AllMin_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.SetMin();
        Recompute();
    }

    private void AllMax_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.SetMax();
        Recompute();
    }

    private void PipMinus_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PowerRow r) { r.Step(-1); Recompute(); }
    }

    private void PipPlus_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PowerRow r) { r.Step(+1); Recompute(); }
    }

    // 全部品から選んだ自作できる部品の品質 (その場だけ。保存しない)。枠・性質の名前は全部の BP の訳 (_names) から引く (候補を替えても訳せるように)
    // 訳が無い・translations.db が替わったときの読み直しは裏のスレッドで行い、済んでから窓を開く (読み直しの最中のクリックは何もしない)
    private async void EditQuality_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PowerRow r || r.QualityInfo is not { HasModifiers: true } info) return;
        if (_qualityOpening) return;
        var selected = r.SelectedCandidate;
        var names = _names;
        var namesStamp = _namesStamp;
        var gamedataPath = GamedataPath;
        var translationsPath = TranslationsPath;
        _qualityOpening = true;
        try
        {
            var loaded = await Task.Run(() =>
            {
                var stamp = NamesStampOf(translationsPath);
                return names != null && namesStamp == stamp ? (Names: names, Stamp: stamp) : (Names: QualityNames.LoadAll(gamedataPath, translationsPath), Stamp: stamp);
            });
            names = loaded.Names;
            // 待つ間に読み込みが訳を読み直していなければ、読んだ訳を覚える
            if (namesStamp == _namesStamp) { _names = names; _namesStamp = loaded.Stamp; }
        }
        catch (Exception ex) { txtStatus.Text = $"品質の名前の訳を読めません: {ex.Message}"; return; }
        finally { _qualityOpening = false; }
        // 読み直しの間に行が作り直された・部品を替えたなら開かない
        if (!_rows.Contains(r) || !ReferenceEquals(r.SelectedCandidate, selected) || !ReferenceEquals(r.QualityInfo, info)) return;
        var dlg = new ComponentQualityDialog(r.SelectedCandidate?.Display ?? "", info, r.Qualities, names) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        r.Qualities = dlg.Result;
        Recompute();
    }

    internal void Recompute()
    {
        if (_rows.Count == 0) { ShowSummary(null); return; }
        var slots = _rows.Where(r => !r.IsPool && r.State != null).Select(r => r.State!).ToList();
        var sum = PowerMathShip.Compute(slots, _pools, _poolRow?.Pips ?? 0);
        foreach (var r in _rows) r.Update(sum, _pools);
        ShowSummary(sum);
        dgWeapons.ItemsSource = _rows.Where(r => r.State?.IsWeapon == true).Select(r => r.WeaponLine(sum)).ToList();
    }

    private void ShowSummary(PowerSummary? s)
    {
        if (s == null)
        {
            foreach (var t in new[] { txtOutput, txtAllocated, txtRemaining, txtCoolant, txtShield, txtWeapons, txtQuantum }) t.Text = "";
            txtFootnote.Text = "";
            return;
        }
        // 言葉はゲームの訳 (global.ini): 割り当て済み = RN_Allocated、不足 = RN_Deficit、冷却剤生成量 = StatName_GPP_ItemResource_CoolantGeneration、
        // 冷却剤使用量 = engineering_ui_Item_CoolantUsage、電力使用量 = engineering_ui_Item_PowerUsage、パワーピップ = StatName_GPP_ItemResource_PowerGeneration、
        // シールド再生量 = RN_Tooltips_ShieldGenerator (の中の「シールド再生量増加」の語)、最大シールド強度 = StatName_GPP_Shield_MaxHealth、
        // 弾薬容量 = weapon_stats_name_capacity、電力喪失 = engineering_ui_Warning_noPower、(生命維持の) 生成量 = RN_LifeSupport_Produced。
        // シールドの回復の待ち (被弾後・ダウン後) は global.ini に語が無い (DamagedRegenDelay・DownedRegenDelay の訳が無い) ので、この画面の言葉
        var anyQuality = _rows.Any(r => r.State?.Qualities != null && r.State.Quality?.HasModifiers == true);
        txtOutput.Text = $"出力 (パワーピップ): {s.Output:0.##}" + (s.OutputEstimated ? " (推定)" : "");
        txtAllocated.Text = $"割り当て済み: {s.Allocated}";
        if (s.Remaining < 0)
        {
            txtRemaining.Text = $"不足: {-s.Remaining:0.##}";
            txtRemaining.Foreground = Brushes.Red;
        }
        else
        {
            txtRemaining.Text = $"残り: {s.Remaining:0.##}";
            txtRemaining.Foreground = Brushes.Black;
        }
        txtCoolant.Text = $"冷却剤生成量: {s.CoolantSupply:#,0.##} /秒" + (s.CoolantEstimated ? " (推定)" : "") + " ・ 冷却剤使用量はデータに式が無く出せない";
        txtShield.Text = s.PoweredShields > 0
            ? $"シールド: 最大シールド強度 {s.ShieldCapacity:#,0}" + (s.ShieldCapacityEstimated ? " (推定)" : "")
              + $" ・ シールド再生量 {s.ShieldRegen:#,0.#} /秒" + (s.ShieldRegenEstimated ? $" (推定。{s.PoweredShields} 基の合計)" : $" ({s.PoweredShields} 基の合計)")
              + $" ・ 再生までの待ち 被弾後 {s.ShieldDamagedDelay:0.##} 秒 / ダウン後 {s.ShieldDownedDelay:0.##} 秒"
            : "シールド: 電力の入ったシールドが無い";
        txtWeapons.Text = $"武器: バースト DPS 合計 {s.BurstTotal:#,0.#}" + (s.BurstEstimated ? " (推定)" : "")
            + $" ・ サステイン DPS 合計 {s.SustainTotal:#,0.#}" + (s.SustainEstimated ? " (推定)" : "")
            + (_pools.WeaponPoolSize is int ps ? $" ・ 武器のプール {_poolRow?.Pips ?? 0}/{ps} ピップ (出力から引く)" : (_pools.WeaponPoolDynamic ? " ・ 武器のプールの大きさがデータに無い (電力 100% として計算)" : ""));
        txtQuantum.Text = s.QuantumDrivePower > 0 ? $"量子ドライブの電力使用量 {s.QuantumDrivePower:0.##} (単位が違うため出力の計算に入れない)" : "";
        txtFootnote.Text =
            "(推定) の付いた値は、ゲームデータの値から計算した推定です (ゲームの中の画面とは確かめていません)。"
            + " クーラー・シールド・生命維持はピップの数ごとの段階の倍率 (powerRanges) を掛けます。武器はプールのピップの割合で武器再生の速さと弾薬容量を比例で減らし、"
            + "バリスティックは熱 (過熱と回復の待ち) を入れます。バリスティック・ビームは電力の割合に関係なく 100% として計算します (推定)。品質の増減は Linear を掛け算、LinearIntegerAdditive を足し算で重ねます。"
            + " スラスター (FlightController) の電力使用量と冷却剤使用量はデータが無いので出しません。"
            + (anyQuality ? " 品質を入れていない枠は品質 500 (ゲームデータの既定の品質) として計算します。" : "");
    }

    // 船セレクタの 1 件
    private sealed class PowerShipChoice
    {
        public string Key = "";
        public string Display { get; set; } = "";
        public string? Record;
        public LoadoutShipChoice? Owned;
        public override string ToString() => Display;
    }
}


// 部品の候補 1 件 (所持コンポーネントなら品質も持つ)
public sealed class PowerCandidate
{
    public string Record { get; set; } = "";
    public string Display { get; set; } = "";
    public Dictionary<string, int>? Qualities { get; set; }
    public bool FromOwned { get; set; }
    public bool IsCurrent { get; set; }   // 最初の部品 (［装備］の現在の装備)
    // 読み込みのときに裏のスレッドで読んでおく値 (候補を選んだときに UI のスレッドで DB を読まないように)
    public PowerItemData? Item { get; set; }
    public BlueprintQualityInfo? Quality { get; set; }
    public string WeaponKind { get; set; } = "";   // 武器の種別の名前 (ダメージの種類。［装備］の表と同じ)
    public override string ToString() => Display;
}

// ［電力管理］の部品の表の 1 行 (武器のプールの行も)
public sealed class PowerRow : INotifyPropertyChanged
{
    public PowerPanel? Panel;
    public ShipPortLoadout? Port;
    public bool IsPool { get; set; }
    public int PoolSize;
    public string PortDisplay { get; set; } = "";
    public string TypeDisplay { get; set; } = "";
    public string ItemType = "";
    public List<PowerCandidate> Candidates { get; } = new();
    public string? CurrentNote;   // 現在の装備の品質を決められなかったときの注 (現在の装備を選んでいる間だけ出す)
    public PowerSlotState? State { get; private set; }
    public BlueprintQualityInfo? QualityInfo => State?.Quality;

    private PowerCandidate? _selected;
    public PowerCandidate? SelectedCandidate
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value) || value == null) return;
            Panel?.SetRowCandidate(this, value);
        }
    }

    // 部品を替える (割り当ては最大から)。値は候補が持つもの (読み込みのときに裏で読んだもの。DB は読まない)
    public void SetCandidate(PowerCandidate c, bool rebuildPips)
    {
        _selected = c;
        var data = c.Item;
        State = new PowerSlotState { PortKey = Port?.PortKey ?? "", Item = data, Quality = c.Quality, Qualities = c.Qualities };
        if (rebuildPips && data != null) State.Pips = data.MaxPips;
        WeaponKindLabel = c.WeaponKind;
        Raise(nameof(SelectedCandidate));
    }

    private string WeaponKindLabel = "";   // 武器の種別の名前 (ダメージの種類。［装備］の表と同じ)

    public Dictionary<string, int>? Qualities
    {
        get => State?.Qualities;
        set { if (State != null) State.Qualities = value; }
    }

    // 割り当て (ピップの部品、または武器のプール)
    public int Pips
    {
        get => IsPool ? _poolPips : State?.Pips ?? 0;
        set { if (IsPool) _poolPips = value; else if (State != null) State.Pips = value; }
    }
    private int _poolPips;

    private int MaxPips => IsPool ? PoolSize : State?.Item?.MaxPips ?? 0;
    private int MinPips => IsPool ? 0 : State?.Item is { } d ? PowerMath.MinPips(d) : 0;
    private bool HasPips => IsPool || State?.IsSegmentConsumer == true;
    private bool IsOnOff => !IsPool && State?.Item is { } d && State.IsSegmentConsumer == false && !State.IsPowerPlant && !State.IsWeapon
        && d.ConsumeUnit == PowerUnitKind.Standard && d.ConsumeAmount > 0;

    // ピップの段: 0 (オフ)、最低のピップ〜最大
    public void Step(int dir)
    {
        if (!HasPips) return;
        var p = Pips + dir;
        if (dir < 0 && p < MinPips) p = 0;
        if (dir > 0 && p > 0 && p < MinPips) p = MinPips;
        Pips = Math.Clamp(p, 0, MaxPips);
    }

    // 全部最小: ピップの部品は動く最低のピップ、武器のプールはゲームの最小の 0 (武器は撃てない。行に出す)、量子ドライブはオン
    public void SetMin() { if (HasPips) Pips = IsPool ? 0 : MinPips; if (IsOnOff && State != null) State.On = true; }
    public void SetMax() { if (HasPips) Pips = MaxPips; if (IsOnOff && State != null) State.On = true; }

    public bool IsOn
    {
        get => State?.On ?? true;
        set { if (State == null || State.On == value) return; State.On = value; Panel?.Recompute(); }
    }

    public Visibility CandidateVisibility => IsPool ? Visibility.Collapsed : Visibility.Visible;
    public Visibility FixedTextVisibility => IsPool ? Visibility.Visible : Visibility.Collapsed;
    public string FixedText => IsPool ? "武器の電力 (FixedPowerPool)" : "";
    public Visibility PipsVisibility => HasPips ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OnOffVisibility => IsOnOff ? Visibility.Visible : Visibility.Collapsed;
    public bool CanMinus => HasPips && Pips > 0;
    public bool CanPlus => HasPips && Pips < MaxPips;
    public string PipsText => HasPips ? (Pips == 0 ? "オフ" : Pips.ToString()) : "";
    public string RangeText => IsPool ? $"0〜{PoolSize}" : HasPips ? $"{MinPips}〜{MaxPips}" : IsOnOff ? "オン/オフ" : "";
    // 品質の欄: 全部品から選んだ自作できる部品は［入力］で入れる。所持コンポーネントは登録した品質を出す (ここでは変えない)
    public string QualityText => PowerMath.QualityLabel(State?.Qualities);
    public bool AllowQualityInput;   // 部品の候補が全部品のときだけ true (所持コンポーネントのときは登録した品質を使う)
    public Visibility QualityButtonVisibility =>
        !IsPool && AllowQualityInput && _selected is { FromOwned: false } && QualityInfo is { HasModifiers: true } ? Visibility.Visible : Visibility.Collapsed;

    public string StageText { get; private set; } = "";
    public string ResultText { get; private set; } = "";
    public string NoteText { get; private set; } = "";

    public void Update(PowerSummary sum, PowerShipPools pools)
    {
        if (IsPool)
        {
            StageText = "";
            ResultText = $"武器の電力 {sum.WeaponFraction:P0}" + (Pips <= 0 ? " (0 ピップでは武器が撃てない)" : "");
            NoteText = "武器全部で 1 本のピップを分け合う (推定)。武器再生の速さと弾薬容量をこの割合で減らす (推定)。出力から引く";
            RaiseAll();
            return;
        }
        var d = State?.Item;
        if (d == null || State == null || !sum.Results.TryGetValue(State, out var r))
        {
            StageText = ""; ResultText = "電力のデータが無い"; NoteText = ""; RaiseAll(); return;
        }
        var notes = new List<string>();
        if (_selected is { IsCurrent: true } && CurrentNote != null) notes.Add(CurrentNote);
        if (d.Priority is int pr) notes.Add($"優先度 {pr}");
        StageText = r.Stage == null ? "" : StageLabel(r.Stage) + $" ×{r.Modifier:0.##}";
        string est = r.Estimated ? " (推定)" : "";
        if (State.IsPowerPlant)
            ResultText = $"パワーピップ {r.Value:0.##}" + est;
        else if (State.IsWeapon && r.Weapon is { } w)
        {
            // 推定は値ごと: バーストは品質、サステインは比例・熱・品質
            ResultText = w.Unpowered ? "オフ (電力喪失)"
                : $"バースト {w.Burst:#,0.#}" + (r.BurstEstimated ? " (推定)" : "")
                  + (w.Sustain is { } sus ? $" ・ サステイン {sus:#,0.#}" + (r.SustainEstimated ? " (推定)" : "") : " ・ サステインはデータなし");
        }
        else if (r.NoPower)
            ResultText = $"電力を受けられない (シールドは {pools.ShieldMaxItems} 基まで。推定)";
        else if (r.Off)
            ResultText = "オフ";
        else if (d.ItemType.Equals("Cooler", StringComparison.OrdinalIgnoreCase))
            ResultText = $"冷却剤生成量 {r.Value:#,0.##} /秒" + est;
        else if (State.IsShield)
            ResultText = $"シールド再生量 {r.Value:#,0.#} /秒" + est + $" ・ 最大シールド強度 {r.ShieldCapacity:#,0}" + (r.CapacityEstimated ? " (推定)" : "");
        else if (d.Generation.ContainsKey("LifeSupport"))
            ResultText = $"生成量 {r.Value:0.###}" + est;   // RN_LifeSupport_Produced
        else if (IsOnOff)
            ResultText = $"電力使用量 {d.ConsumeAmount:0.##} (単位が違うため出力の計算に入れない)";
        else
            ResultText = "";
        if (r.Stage != null && r.Modifier != 1) notes.Add("段階の倍率を掛けた値 (推定)");
        if (r.QualityApplied) notes.Add(State.IsPowerPlant ? $"品質 {r.QualityAdditive:+0;-0;±0} ピップ (推定)" : $"品質 ×{r.QualityMultiplier:0.####} (推定)");
        if (State.IsWeapon && r.Weapon is { SustainEstimated: true }) notes.Add(r.Weapon.Kind == "Ballistic" ? "熱を入れたサステイン (推定)" : "武器のピップに比例 (推定)");
        if (State.IsWeapon && r.Weapon is { PowerAssumedFull: true }) notes.Add(PowerAssumedFullNote);
        if (State.IsSegmentConsumer && d.Ranges.Count(x => x.Registered) == 1 && d.MaxPips > 1)
            notes.Add("段階が 1 つだけ (ピップを減らしても倍率は変わらない)");
        NoteText = string.Join(" ・ ", notes);
        RaiseAll();
    }

    // 段階の表示: low = L (RN_PipIndicator_Low)、high = H (RN_PipIndicator_High)。medium はゲームの訳に字が無いので倍率だけ
    private static string StageLabel(PowerRange s) => s.Name switch { "low" => "L", "high" => "H", _ => "" };

    // バリスティック・ビームを、武器のプールの割合が 100% より少ないのに 100% として計算したときの注
    private const string PowerAssumedFullNote = "電力の割合で変わるかはデータに無い (100% として計算。推定)";

    public PowerWeaponLine WeaponLine(PowerSummary sum)
    {
        var line = new PowerWeaponLine { Name = SelectedCandidate?.Display ?? "" };
        var d = State?.Item;
        if (d?.Weapon is not { } w || State == null || !sum.Results.TryGetValue(State, out var r) || r.Weapon is not { } res) return line;
        line.Kind = (WeaponKindLabel.Length > 0 ? WeaponKindLabel : "武器")
            + (w.Kind == "Energy" ? " (武器再生)" : w.Kind == "Ballistic" ? " (弾薬)" : w.Kind == "Beam" ? " (ビーム)" : "");
        line.PerShot = w.DamagePerShot > 0 ? $"{w.DamagePerShot * w.Pellets * r.QualityMultiplier:#,0.##}" + (r.QualityApplied ? " (推定)" : "") : "";
        line.Rate = w.RatePerMinute > 0 ? $"{w.RatePerMinute:0.##}" : "";
        if (res.Unpowered) { line.Burst = "オフ"; line.Note = "電力喪失 (connectionParams.noPowerStats の連射の倍率 0)"; return line; }
        line.Burst = res.Burst > 0 ? $"{res.Burst:#,0.#}" + (r.BurstEstimated ? " (推定)" : "") : "";
        // 推定の印: エネルギーは割合で比例で減らした値 (PowerScaled)、バリスティックは熱の式 (ShotsToOverheat) と割合を 100% とした値 (PowerAssumedFull)。
        // 待ち (regenerationCooldown・overheatFixTime) はデータの値のまま
        string est = res.PowerScaled ? " (推定)" : "";
        line.Ammo = res.Ammo > 0 ? $"{res.Ammo:#,0.##}" + est : "";
        line.Regen = res.RegenPerSec > 0 ? $"{res.RegenPerSec:0.##}" + est : "";
        line.Wait = res.Wait > 0 ? $"{res.Wait:0.###}" : "";
        line.Cycle = res.CycleSeconds > 0 ? $"{res.CycleSeconds:0.##}" + (res.PowerScaled || res.ShotsToOverheat > 0 || res.PowerAssumedFull ? " (推定)" : "") : "";
        line.Sustain = res.Sustain is { } s ? $"{s:#,0.#}" + (r.SustainEstimated ? " (推定)" : "") : "データなし";
        line.Empty = res.EmptySeconds > 0 ? $"{res.EmptySeconds:#,0.#}" + (res.PowerScaled || res.ShotsToOverheat > 0 || res.PowerAssumedFull ? " (推定)" : "") : "";
        var notes = new List<string>();
        if (w.Kind == "Energy") notes.Add("弾薬容量を撃つ → 待ち → 満タンまで武器再生 を繰り返す");
        if (w.Kind == "Ballistic")
            notes.Add(res.ShotsToOverheat > 0
                ? $"{res.ShotsToOverheat:0} 発で過熱 → {w.Heat!.OverheatFixTime:0.##} 秒待ち。総ダメージ {res.TotalDamage:#,0}"
                : $"総ダメージ {res.TotalDamage:#,0}");
        if (w.Kind == "Beam") notes.Add("ビームはバーストだけ");
        if (w.Kind.Length == 0) notes.Add("弾・武器再生のデータが無い");
        if (res.PowerAssumedFull) notes.Add(PowerAssumedFullNote);
        line.Note = string.Join(" ・ ", notes);
        return line;
    }

    private void RaiseAll()
    {
        foreach (var n in new[] { nameof(StageText), nameof(ResultText), nameof(NoteText), nameof(PipsText), nameof(CanMinus), nameof(CanPlus), nameof(RangeText),
                     nameof(QualityText), nameof(QualityButtonVisibility), nameof(PipsVisibility), nameof(OnOffVisibility), nameof(IsOn) })
            Raise(n);
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}

// ［電力管理］の武器の表の 1 行
public sealed class PowerWeaponLine
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string PerShot { get; set; } = "";
    public string Rate { get; set; } = "";
    public string Burst { get; set; } = "";
    public string Ammo { get; set; } = "";
    public string Regen { get; set; } = "";
    public string Wait { get; set; } = "";
    public string Cycle { get; set; } = "";
    public string Sustain { get; set; } = "";
    public string Empty { get; set; } = "";
    public string Note { get; set; } = "";
}
