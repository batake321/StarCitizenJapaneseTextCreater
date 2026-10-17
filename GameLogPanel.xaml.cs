using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

// ゲームのログ (［ゲームログ］タブ)。ゲームのフォルダ (App.Config.GamePath。例 E:\Games\RSI\StarCitizen\LIVE) の
//   Game.log: 今のログ。1 秒ごとに増えた分だけを読む (GameLogTail)。ゲームの再起動で読み直しになったら、
//             それまでの「現在」の行を捨て、logbackups に移された前回のログを過去のログとして読み足す
//   logbackups\*.log: 過去のログ。初回表示時に裏ですべて読む
// から出来事を拾って (GameLogParser) 1 つの表に新しい順で出す。
// ミッション名 (MissionId → 名前) はタブ全体で 1 つを共有し (GameLogMissionNames)、ファイルをまたいで引く。名前待ちの行の書き換えは UI のスレッドで行う。
// 船名・品名は gamedata_cache.db / translations.db (作業フォルダ。読み取り専用) で日本語にする (引けなければ内部名)。
// 確定した行は trade_cache.db (game_log_entries。GameLogStore) に残し (StoredSaver)、ほかの PC で残した行 (StoredLoader) を手元の行と合わせて表に出す
// (バックアップ・PC 間の同期で DB ごと渡る)。DB の行でも「所持」のボタン・ダブルクリック・利益の添え書きは手元の行と同じように効く
public partial class GameLogPanel : UserControl
{
    private const string CurrentSource = "現在";
    // 読み直し (ゲームの再起動) の後に logbackups の新しいファイルを探す回数。タイマーの 1 回 (1 秒) ごとに 1 回探す (最大で約 60 秒)。
    // 見つかったらやめる。ゲームが Game.log を移すのが少し遅れる場合に備える
    private const int RescanTries = 60;

    // 表の列を入れたか (タブの初回表示で)。読み込みを始めたか (EnsureStarted。タブを開いていなくても MainWindow の Window_Loaded から始める)
    private bool _columnsSet;
    private bool _started;

    // Game.log が最初から読み直しになった (ゲームの再起動) ときに UI のスレッドで知らせる (最初の読み込みでは出ない)
    public event Action? GameLogReset;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _polling;
    // 追い読みで新しく足された行から鳴らす音を決める (休戦ゾーンに進入・到着)。設定の有効/無効とファイルは鳴らすときに App.Config から読む
    private readonly GameLogSoundJudge _soundJudge = new();

    private GameLogNames? _names;
    private GameLogTail? _tail;
    private readonly GameLogMissionNames _missionNames = new();
    private int _missionVersionApplied = -1;
    // 着陸場所の ID → 場所の名前 (ミッション名と同じくタブ全体で 1 つを共有し、名前待ちの行の書き換えは UI のスレッドで行う)
    private readonly GameLogPlaceNames _placeNames = new();
    private int _placeVersionApplied = -1;
    // 船の ID → クラス名・推定の系統 (同じく共有し、船名待ちの呼び出しの行の書き換えは UI のスレッドで行う)
    private readonly GameLogVehicleClasses _vehicleClasses = new();
    private int _vehicleVersionApplied = -1;
    private string _logPath = "";
    private bool _logMissing;
    private string? _pollError;
    private string? _startError;   // 読み込みを始められなかった理由 (ゲームのフォルダが無い)。状態の 1 行目に出す
    private GameLogEntry[] _currentRows = [];

    // trade_cache.db に残した行 (game_log_entries。GameLogStore)。
    //   StoredLoader: 残してある行の全部 (保存した PC の名前つき) と、読めなかった行の数 (MainWindow → HangarService。裏のスレッドで呼ぶ)
    //   StoredSaver:  手元の確定済みの行の写し (GameLogStore.Row) を残す (INSERT OR IGNORE。入れた行数を返す。MainWindow → HangarService。
    //                 UI のスレッドから呼び、保存は裏のスレッドで行われる (Task))
    // 表には自分以外の PC の行を手元の行と合わせて出す (手元の Game.log が無ければ自分の行も)。同じ鍵は手元の行を優先 (GameLogStore.Merge)
    public Func<(List<StoredGameLogEntry> Rows, int Broken)>? StoredLoader { get; set; }
    public Func<IReadOnlyList<GameLogStore.Row>, Task<int>>? StoredSaver { get; set; }
    private static readonly string OwnMachine = Environment.MachineName;
    private StoredGameLogEntry[] _storedRows = [];   // UI のスレッドだけで使う
    private int _storedShown;                         // 表に合わせて出している DB の行の数 (状態の表示用)
    private bool _storedLoading;
    private bool _storedReloadQueued;
    // 残した行の鍵 (GameLogStore.Key。同じ行は起動のあいだ 1 回だけ渡す) と、まだ確定していない手元の行 (追い読みのたびに確定したものから残す)
    private readonly HashSet<string> _savedKeys = new(StringComparer.Ordinal);
    private List<GameLogEntry> _unsettled = new();
    // 保存中・保存待ちの行 (SaveToStore。保存は裏で 1 回ずつ行い、保存中に頼まれた分は終わってから次に回す)。_savingKeys はその鍵 (二重に渡さないため)
    private readonly HashSet<string> _savingKeys = new(StringComparer.Ordinal);
    private readonly List<GameLogEntry> _storePending = new();
    private bool _storeSaving;

    // 過去のログ (裏のスレッドで足す。_backupLock で守る)
    private readonly object _backupLock = new();
    private readonly List<GameLogEntry> _backupRows = new();
    private bool _backupChanged;
    private readonly HashSet<string> _knownBackups = new(StringComparer.OrdinalIgnoreCase);     // 一覧で見たファイル (初回と、再起動の後に探したとき)
    private readonly HashSet<string> _claimedBackups = new(StringComparer.OrdinalIgnoreCase);   // 読み始めたファイル (同じファイルを二重に読まない)
    private int _backupTotal, _backupDone, _backupFailed;
    private bool _initialBackupDone;
    private string? _backupError;
    // UI のスレッドだけで使う
    private GameLogEntry[] _backupSnapshot = [];
    private string _backupDir = "";
    private bool _backupDirMissing;
    private int _rescanLeft;
    private bool _rescanRunning;

    // 所持・在庫に入れられる行 (購入・採掘・貨物エレベーターで降ろした品。GameLogEntry.ImportItems あり) のボタン「入れる」「取り消す」(MainWindow が渡す)。
    //   ImportClassifier: その行の一覧 → それぞれのボタンの状態 (None = 出さない、Ready = 「入れる」、Imported = 「取り消す」)。null なら、どの行にもボタンを出さない
    //   Importer:         1 行の品を全部入れる (印は品ごと)。Canceller: 1 行の印を消して所持・在庫から引く。どちらも後で表を作り直す
    public Func<IReadOnlyList<GameLogEntry>, IReadOnlyList<PurchaseImportState>>? ImportClassifier { get; set; }
    public Action<GameLogEntry>? Importer { get; set; }
    public Action<GameLogEntry>? Canceller { get; set; }
    // 売買・採掘の記録の自動取り込み (MainWindow → OrePanel.ImportLogRecords)。表を作り直すたびに、まだ渡していない行だけを渡す。
    // TradeResultUpdater: 渡した後に取引の結果 (失敗) が結び付いた行。SellProfit: 売却の行の鍵 (GameLogTrade.Key) → (利益, 利益率) (計算前なら null)
    public Action<IReadOnlyList<GameLogTrade>, IReadOnlyList<GameLogMining>>? RecordsImporter { get; set; }
    public Action<GameLogTrade>? TradeResultUpdater { get; set; }
    // 納品の行 (kind delivered の品) の自動の減算 (MainWindow)。まだ渡していない行だけを渡す
    public Action<IReadOnlyList<GameLogEntry>>? DeliveryImporter { get; set; }
    private readonly HashSet<string> _handedDeliveries = new(StringComparer.Ordinal);
    // 船の行 (船の購入・呼び出し。ImportItems の品が船 (IsShip) でクラス名の分かるもの) の自動の所持船への登録 (MainWindow)。まだ渡していない行だけを渡す。
    // false を返したら (所持船の情報がまだ読めていない) 「渡した」にせず、次の表の作り直しでもう一度渡す
    public Func<IReadOnlyList<GameLogEntry>, bool>? ShipImporter { get; set; }
    private readonly HashSet<string> _handedShips = new(StringComparer.Ordinal);
    // BP受信の行 (BP を同定できたもの) の自動の所持の BP への登録 (MainWindow)。まだ渡していない行だけを渡す (鍵は GameLogBlueprintReceipt.LogKey)。
    // false を返したら「渡した」にせず、次の表の作り直しでもう一度渡す
    public Func<IReadOnlyList<GameLogEntry>, bool>? BlueprintImporter { get; set; }
    private readonly HashSet<string> _handedBlueprints = new(StringComparer.Ordinal);
    public Func<string, (double Profit, double? Rate)?>? SellProfit { get; set; }
    // 内容の欄の品名のダブルクリック → ［詳細］(MainWindow が DetailPanel.Request / RequestSearch に回す)
    public Action<GameLogImportItem>? DetailOpener { get; set; }
    // 内容の欄の船のダブルクリック (品名の無い行の船。クラス名 (分かっていれば) と表示名) → ［詳細］
    public Action<string?, string>? ShipDetailOpener { get; set; }
    // 内容の欄の BP受信の行 (BP を同定できたもの) のダブルクリック (できる品の record と通知の品名) → ［詳細］
    public Action<string, string>? BlueprintDetailOpener { get; set; }

    // 渡した売買の記録 (鍵 → そのときの結果) と採掘の記録 (鍵)。UI のスレッドだけで使う
    private readonly Dictionary<string, string> _handedTrades = new(StringComparer.Ordinal);
    private readonly HashSet<string> _handedMining = new(StringComparer.Ordinal);

    public GameLogPanel()
    {
        InitializeComponent();
        table.CellDoubleClicked += OnCellDoubleClicked;
        _timer.Tick += async (_, _) => await PollAsync();
        Loaded += (_, _) =>
        {
            var columnsJustSet = false;
            if (!_columnsSet)
            {
                _columnsSet = true;
                // 表の部品の例外でもアプリを落とさず、1 行の知らせにする
                try { table.SetColumns(Columns()); }
                catch (Exception ex) { ShowMessage($"読み込めませんでした: {ex.Message}"); return; }
                columnsJustSet = true;
            }
            EnsureStarted();
            // 読み込みが先に始まっていた (Window_Loaded の EnsureStarted) ときは、列の無いまま入れた行を列つきで作り直す
            // (SetItems は列が決まってから入れないと空のセルになり、SetColumns は行を作り直さない)。DB の行だけのとき (ゲームのフォルダが無い PC) も同じ
            if (columnsJustSet && (_tail != null || _storedRows.Length > 0)) UpdateTable();
        };
        // タイマーを止めるのはアプリの終了時だけ
        Dispatcher.ShutdownStarted += (_, _) => _timer.Stop();
    }

    // 読み込み (Start) と 1 秒ごとの追い読みを始める (まだなら)。タブを開いていなくても音が鳴るよう MainWindow の Window_Loaded からも呼ぶ。
    // 二重には始めない。以後はタブを表示していない間も 1 秒ごとに読む
    public void EnsureStarted()
    {
        if (!_started)
        {
            _started = true;
            // DB に残した行 (ほかの PC の行) は、ゲームのフォルダが無い PC でも出す
            ReloadStored();
            try
            {
                if (!Start()) return;
            }
            catch (Exception ex) { ShowMessage($"読み込めませんでした: {ex.Message}"); return; }
        }
        if (_tail == null || _timer.IsEnabled) return;
        _timer.Start();
        _ = PollAsync();
    }

    private static string WorkDir()
    {
        var workDir = App.Config.WorkingDirectory;
        if (string.IsNullOrEmpty(workDir)) workDir = AppDomain.CurrentDomain.BaseDirectory;
        return workDir;
    }

    // 列: 日時 (ローカル時刻)、種類、内容、金額、ログ (どのファイル由来か。今の Game.log は「現在」)、所持 (購入・採掘・降ろした品の行の「入れる」「取り消す」)
    private const int ContentColumn = 2;

    private List<GridColumnSpec> Columns() =>
    [
        new()
        {
            Header = "日時", Filterable = false,
            Text = o => ((GameLogEntry)o).TimeUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss"),
            Key = o => ((GameLogEntry)o).TimeUtc,
        },
        new() { Header = "種類", Text = o => ((GameLogEntry)o).Kind, Key = o => ((GameLogEntry)o).Kind is { Length: > 0 } s ? s : null },
        new() { Header = "内容", Filterable = false, Text = o => ContentText((GameLogEntry)o), Key = o => ContentText((GameLogEntry)o) is { Length: > 0 } s ? s : null },
        // 数値の列には絞り込みのボタン (Select) を出さない (並べ替えはできる)
        new()
        {
            Header = "金額", Numeric = true, Filterable = false,
            Text = o => ((GameLogEntry)o).Amount?.ToString("#,0.##") ?? "", Key = o => ((GameLogEntry)o).Amount,
        },
        new() { Header = "ログ", Text = o => ((GameLogEntry)o).Source, Key = o => ((GameLogEntry)o).Source is { Length: > 0 } s ? s : null },
        // 所持: 所持・在庫に入れられる行に「入れる」、入れた後は「取り消す」(印を消して所持・在庫から引く)
        new()
        {
            Header = "所持",
            Text = o => ButtonText((GameLogEntry)o),
            Key = o => ButtonText((GameLogEntry)o) is { Length: > 0 } s ? s : null,
            Button = o => ((GameLogEntry)o).ImportState switch { PurchaseImportState.Ready => true, PurchaseImportState.Imported => true, _ => null },
            ButtonClick = o => OnImportButton((GameLogEntry)o),
        },
    ];

    // 内容の欄の文字。売却の行 (成功したもの) には、［鉱石］の売買の記録で計算した利益を添える (計算前なら添えない。利益率が無ければ利益だけ)
    private string ContentText(GameLogEntry e)
    {
        if (e.Trade is not { Kind: "sell" } t || t.Result.Length > 0 || SellProfit == null) return e.Content;
        (double Profit, double? Rate)? p;
        try { p = SellProfit(t.Key); }
        catch { return e.Content; }
        if (p is not { } v) return e.Content;
        var text = $"{e.Content} / 利益 {v.Profit:+#,0;-#,0;0} aUEC";
        if (v.Rate is { } r) text += $" ({r.ToString("0.0%", System.Globalization.CultureInfo.InvariantCulture)})";
        return text;
    }

    // ボタンの文字: Ready は「入れる」(納品の行 (在庫から引く) は「減らす」)、Imported は「取り消す」
    private static string ButtonText(GameLogEntry e) => e.ImportState switch
    {
        PurchaseImportState.Ready => e.ImportItems is { Count: > 0 } items && items.All(i => i.Kind == GameLogImportItem.KindDelivered) ? "減らす" : "入れる",
        PurchaseImportState.Imported => "取り消す",
        _ => "",
    };

    // 「入れる」「取り消す」が押されたとき: MainWindow に入れて (取り消して) もらい、ボタンの状態を作り直す
    private void OnImportButton(GameLogEntry e)
    {
        if (e.ImportState == PurchaseImportState.Ready) Importer?.Invoke(e);
        else if (e.ImportState == PurchaseImportState.Imported) Canceller?.Invoke(e);
        else return;
        UpdateTable();
    }

    // 内容の欄のダブルクリック: 押した位置にある品名の品 (品が 1 つの行はどこを押しても) を［詳細］に出す。
    // 品名の無い行 (品が船だけの行も含む) は、行の船 (ShipClass / ShipName) を［詳細］に出す (船名がまだ分からない行は何もしない)
    private void OnCellDoubleClicked(int column, object source, int charIndex)
    {
        if (column != ContentColumn || source is not GameLogEntry e) return;
        // BP受信の行: 同定した BP (先頭) のできる品を［詳細］に出す (同定できなかった行は何もしない)
        if (e.Blueprint is { } bp)
        {
            if (bp.Matches.Count > 0 && bp.Matches[0].OutputRecord.Length > 0) BlueprintDetailOpener?.Invoke(bp.Matches[0].OutputRecord, bp.Name);
            return;
        }
        var items = e.ImportItems is { Count: > 0 } list ? list.Where(i => !i.IsShip).ToList() : [];
        if (items.Count > 0 && DetailOpener != null)
        {
            GameLogImportItem? hit = null;
            if (charIndex >= 0)
            {
                foreach (var item in items)
                {
                    if (item.DisplayName.Length == 0) continue;
                    var at = e.Content.IndexOf(item.DisplayName, StringComparison.Ordinal);
                    if (at >= 0 && charIndex >= at && charIndex <= at + item.DisplayName.Length) { hit = item; break; }
                }
            }
            hit ??= items.Count == 1 ? items[0] : null;
            if (hit != null) DetailOpener(hit);
            return;
        }
        if (ShipDetailOpener == null) return;
        var name = e.ShipName ?? (e.ShipClass != null ? _names?.Ship(e.ShipClass) : null);
        if (e.ShipClass == null && string.IsNullOrEmpty(name)) return;
        ShipDetailOpener(e.ShipClass, name ?? e.ShipClass!);
    }

    // 所持・在庫に入れられる行のボタンの状態を決める (ImportClassifier)。失敗したらボタンを出さない
    private void ClassifyImports(IReadOnlyList<GameLogEntry> entries)
    {
        var rows = entries.Where(e => e.ImportItems is { Count: > 0 }).ToList();
        foreach (var e in rows) e.ImportState = PurchaseImportState.None;
        if (ImportClassifier == null || rows.Count == 0) return;
        try
        {
            var states = ImportClassifier(rows);
            for (int i = 0; i < rows.Count && i < states.Count; i++) rows[i].ImportState = states[i];
        }
        catch { foreach (var e in rows) e.ImportState = PurchaseImportState.None; }
    }

    // 売買・採掘の記録を［鉱石］へ渡す (まだ渡していない行だけ。古い順)。渡した後に結果 (失敗) が結び付いた売買は TradeResultUpdater へ。
    // 失敗しても表は作る (次の作り直しでもう一度渡す)
    private void HandOverRecords(IReadOnlyList<GameLogEntry> entries)
    {
        if (RecordsImporter == null) return;
        var trades = new List<GameLogTrade>();
        var mining = new List<GameLogMining>();
        var changed = new List<GameLogTrade>();
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var e = entries[i];
            if (e.Trade is { } t)
            {
                if (!_handedTrades.TryGetValue(t.Key, out var prev)) trades.Add(t);
                else if (prev != t.Result) changed.Add(t);
            }
            if (e.Mining is { } m && !_handedMining.Contains(m.Key)) mining.Add(m);
        }
        try
        {
            if (trades.Count > 0 || mining.Count > 0) RecordsImporter(trades, mining);
            foreach (var t in trades) _handedTrades[t.Key] = t.Result;
            foreach (var m in mining) _handedMining.Add(m.Key);
            if (TradeResultUpdater != null)
            {
                foreach (var t in changed)
                {
                    TradeResultUpdater(t);
                    _handedTrades[t.Key] = t.Result;
                }
            }
        }
        catch (Exception ex)
        {
            // 渡せなかった行は「渡した」にしない (次の作り直しでもう一度渡す)。失敗はログに残す
            ChatService.LogExternal($"[ゲームログ] 売買・採掘の記録を［鉱石］へ渡せませんでした (次の表の作り直しでもう一度渡します): {ex.Message}");
        }
    }

    // 納品の行 (kind delivered の品) をまだ渡していないものだけ DeliveryImporter へ (古い順)。失敗したら「渡した」にせず、次の作り直しでもう一度渡す
    private void HandOverDeliveries(IReadOnlyList<GameLogEntry> entries)
    {
        if (DeliveryImporter == null) return;
        var list = new List<GameLogEntry>();
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var e = entries[i];
            if (e.ImportItems is not { Count: > 0 } items || items[0].Kind != GameLogImportItem.KindDelivered) continue;
            if (!_handedDeliveries.Contains(items[0].LogKey)) list.Add(e);
        }
        if (list.Count == 0) return;
        try
        {
            DeliveryImporter(list);
            foreach (var e in list) _handedDeliveries.Add(e.ImportItems![0].LogKey);
        }
        catch (Exception ex)
        {
            ChatService.LogExternal($"[ゲームログ] 納品の在庫への反映に失敗しました (次の表の作り直しでもう一度行います): {ex.Message}");
        }
    }

    // 船の行 (購入した船・呼び出した船。品が船 (IsShip) でクラス名が分かっていて、購入なら成功したもの) をまだ渡していないものだけ ShipImporter へ (古い順)。
    // 渡す条件を満たすまで (船名が分かるまで・購入の結果が来るまで) は「渡した」にしない。失敗したら「渡した」にせず、次の作り直しでもう一度渡す
    private void HandOverShips(IReadOnlyList<GameLogEntry> entries)
    {
        if (ShipImporter == null) return;
        var list = new List<GameLogEntry>();
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var e = entries[i];
            if (e.ImportItems is not { Count: > 0 } items || !items[0].IsShip || items[0].ItemClass.Length == 0) continue;
            if (e.Purchase is { } p && !p.Succeeded) continue;
            if (!_handedShips.Contains(items[0].LogKey)) list.Add(e);
        }
        if (list.Count == 0) return;
        try
        {
            if (!ShipImporter(list)) return;
            foreach (var e in list) _handedShips.Add(e.ImportItems![0].LogKey);
        }
        catch (Exception ex)
        {
            ChatService.LogExternal($"[ゲームログ] 船の所持船への登録に失敗しました (次の表の作り直しでもう一度行います): {ex.Message}");
        }
    }

    // BP受信の行 (BP を同定できたもの) をまだ渡していないものだけ BlueprintImporter へ (古い順)。
    // 失敗したら「渡した」にせず、次の作り直しでもう一度渡す
    private void HandOverBlueprints(IReadOnlyList<GameLogEntry> entries)
    {
        if (BlueprintImporter == null) return;
        var list = new List<GameLogEntry>();
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var e = entries[i];
            if (e.Blueprint is not { } bp || _handedBlueprints.Contains(bp.LogKey)) continue;
            // 解析のときに同定できなかった行 (gamedata_cache.db が無い・作り直し中など) は、渡す前にもう一度品名から同定してみる。
            // 決まれば行の BP を埋めて渡し、決まらなければ渡さない (次の作り直しでまた試す)
            if (bp.Matches.Count == 0 && _names is { } names)
            {
                try
                {
                    if (names.Blueprints(bp.Name) is { Count: > 0 } found) bp.Matches.AddRange(found);
                }
                catch { }   // DB を読めない: 次の作り直しでまた試す
            }
            if (bp.Matches.Count > 0) list.Add(e);
        }
        if (list.Count == 0) return;
        try
        {
            if (!BlueprintImporter(list)) return;
            foreach (var e in list) _handedBlueprints.Add(e.Blueprint!.LogKey);
        }
        catch (Exception ex)
        {
            ChatService.LogExternal($"[ゲームログ] BP の所持への登録に失敗しました (次の表の作り直しでもう一度行います): {ex.Message}");
        }
    }

    // 船名・品名の覚えた結果を捨てる。MainWindow から、訳・品目名 (item_index.name) が替わったときに呼ぶ。
    // これから読む行の名前は今の訳で引き直す (もう読んだ行の名前は読んだときのまま)
    public void ClearNameCache() => _names?.Clear();

    // 表を作り直す (ボタンの状態も決め直す)。MainWindow から、所持コンポーネントが変わったときなどに呼ぶ
    public void RefreshPurchaseButtons()
    {
        if (_tail == null && _storedRows.Length == 0) return;
        UpdateTable();
    }

    // DB に残した行 (game_log_entries) を読み直す (起動時 (EnsureStarted) と、バックアップの取り込み・PC 間の同期の後に MainWindow から)。
    // 裏のスレッドで読み、終わったら表を作り直す。読んでいる間に頼まれたら、終わってからもう一度読む
    public void ReloadStored()
    {
        if (StoredLoader is not { } loader) return;
        if (_storedLoading)
        {
            _storedReloadQueued = true;
            return;
        }
        _storedLoading = true;
        _ = LoadStoredAsync(loader);
    }

    private async Task LoadStoredAsync(Func<(List<StoredGameLogEntry> Rows, int Broken)> loader)
    {
        try
        {
            var (rows, broken) = await Task.Run(loader);
            _storedRows = rows.ToArray();
            if (broken > 0) ChatService.LogExternal($"[ゲームログ] trade_cache.db に残した行のうち {broken} 件は読めなかったので飛ばしました");
            UpdateTable();
        }
        catch (Exception ex)
        {
            ChatService.LogExternal($"[ゲームログ] trade_cache.db に残した行を読めませんでした: {ex.Message}");
        }
        finally { _storedLoading = false; }
        UpdateStatus();
        if (_storedReloadQueued)
        {
            _storedReloadQueued = false;
            ReloadStored();
        }
    }

    // 手元の行 (新しい順) のうち、まだ残していない確定済みの行 (GameLogStore.IsSettled: 名前待ちでなく、今の Game.log の行は時刻から SettleWindow 過ぎたもの) を
    // DB に残す (古い順)。まだ確定していない行は覚えておき、追い読みのたびに確定したものから残す (FlushUnsettled)
    private void SaveSettled(IReadOnlyList<GameLogEntry> local)
    {
        _unsettled = new List<GameLogEntry>();
        if (StoredSaver == null) return;
        var now = DateTime.UtcNow;
        var list = new List<GameLogEntry>();
        for (int i = local.Count - 1; i >= 0; i--)
        {
            var e = local[i];
            if (!GameLogStore.IsSettled(e, e.Source == CurrentSource, now)) _unsettled.Add(e);
            else if (!IsSavedOrSaving(e)) list.Add(e);
        }
        SaveToStore(list);
    }

    private bool IsSavedOrSaving(GameLogEntry e)
    {
        var key = GameLogStore.Key(e);
        return _savedKeys.Contains(key) || _savingKeys.Contains(key);
    }

    // 表を作り直さなかった追い読みの回でも、時間が経って確定した行を残す
    private void FlushUnsettled()
    {
        if (_unsettled.Count == 0 || StoredSaver == null) return;
        var now = DateTime.UtcNow;
        var list = new List<GameLogEntry>();
        var rest = new List<GameLogEntry>();
        foreach (var e in _unsettled)
        {
            if (!GameLogStore.IsSettled(e, e.Source == CurrentSource, now)) rest.Add(e);
            else if (!IsSavedOrSaving(e)) list.Add(e);
        }
        if (list.Count == 0 && rest.Count == _unsettled.Count) return;
        _unsettled = rest;
        SaveToStore(list);
    }

    // 行を DB に残す (StoredSaver。INSERT OR IGNORE なので既にある行は入らない)。残した鍵は覚えて、同じ行は起動のあいだ 1 回だけ渡す。
    // 行の写し (GameLogStore.ToRow。payload_json まで) を UI のスレッドで取り、保存は裏 (StoredSaver の Task) で行う。
    // 保存中にもう一度頼まれたら重ねず、終わってから次の分を保存する。残した鍵は保存が終わってから UI のスレッドで _savedKeys に入れる。
    // 失敗したら「残した」にせず、次の表の作り直しでもう一度渡す
    private void SaveToStore(List<GameLogEntry> list)
    {
        if (list.Count == 0 || StoredSaver == null) return;
        foreach (var e in list) _savingKeys.Add(GameLogStore.Key(e));
        _storePending.AddRange(list);
        if (_storeSaving) return;
        _ = SaveStoreLoopAsync();
    }

    private async Task SaveStoreLoopAsync()
    {
        _storeSaving = true;
        try
        {
            while (_storePending.Count > 0 && StoredSaver is { } saver)
            {
                var batch = _storePending.ToList();
                _storePending.Clear();
                var rows = batch.Select(GameLogStore.ToRow).ToList();   // 写し (UI のスレッドで)
                try
                {
                    await saver(rows);
                    foreach (var e in batch)
                    {
                        var key = GameLogStore.Key(e);
                        _savingKeys.Remove(key);
                        _savedKeys.Add(key);
                    }
                }
                catch (Exception ex)
                {
                    foreach (var e in batch) _savingKeys.Remove(GameLogStore.Key(e));
                    ChatService.LogExternal($"[ゲームログ] 行を trade_cache.db に残せませんでした (次の表の作り直しでもう一度残します): {ex.Message}");
                }
            }
        }
        finally { _storeSaving = false; }
    }

    // 読み込みを始める。ゲームのフォルダが無ければ案内を出して false
    private bool Start()
    {
        var gamePath = App.Config.GamePath;
        if (string.IsNullOrEmpty(gamePath) || !Directory.Exists(gamePath))
        {
            _startError = "ゲームのフォルダが見つかりません (設定タブのゲームのフォルダを確認してください)";
            UpdateStatus();
            return false;
        }
        var workDir = WorkDir();
        _names = new GameLogNames(Path.Combine(workDir, "gamedata_cache.db"), Path.Combine(workDir, "translations.db"));
        _logPath = Path.Combine(gamePath, "Game.log");
        _tail = new GameLogTail(_logPath, CurrentSource, _names.Ship, _names.Item, _missionNames, _names.Translate, _names.IsVehicleClass, _names.Resource,
            _names.Place, _placeNames, _names.ItemLabel, _vehicleClasses, _names.ShipClassByName, _names.Blueprints, _names.BlueprintHeadings());

        // 過去のログ: 初回の一覧はここで取り (再起動の後に探すときに、新しいファイルと見分けるため)、読むのは裏で行う
        _backupDir = Path.Combine(gamePath, "logbackups");
        _backupDirMissing = !Directory.Exists(_backupDir);
        List<FileInfo> files = [];
        try { files = ListBackups(); }
        catch (Exception ex) { _backupError = ex.Message; }
        lock (_backupLock)
        {
            foreach (var f in files) _knownBackups.Add(f.FullName);
            _backupTotal = files.Count;
        }
        var names = _names;
        _ = Task.Run(() => ReadBackups(files, names, initial: true));
        UpdateStatus();
        return true;
    }

    // logbackups の *.log (新しい順)。フォルダが無ければ空
    private List<FileInfo> ListBackups() =>
        Directory.Exists(_backupDir)
            ? new DirectoryInfo(_backupDir).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).ToList()
            : [];

    // 過去のログを読む (裏のスレッド)。1 ファイルごとに状態を出す。表へは次のタイマーで反映する。
    // まだ読み始めていないファイルだけを読む (初回の読み込みと再起動の後の読み足しが重なっても二重に読まない)
    private void ReadBackups(List<FileInfo> files, GameLogNames names, bool initial)
    {
        foreach (var f in files)
        {
            lock (_backupLock)
            {
                if (!_claimedBackups.Add(f.FullName)) continue;
            }
            try
            {
                var rows = GameLogParser.ParseFile(f.FullName, Path.GetFileNameWithoutExtension(f.Name), names.Ship, names.Item, _missionNames,
                    names.Translate, names.IsVehicleClass, names.Resource, names.Place, _placeNames, names.ItemLabel, _vehicleClasses, names.ShipClassByName,
                    names.Blueprints, names.BlueprintHeadings());
                lock (_backupLock)
                {
                    _backupRows.AddRange(rows);
                    _backupChanged = true;
                    _backupDone++;
                }
            }
            catch
            {
                lock (_backupLock)
                {
                    _backupFailed++;
                    _backupDone++;
                }
            }
            Dispatcher.InvokeAsync(UpdateStatus);
        }
        if (initial)
        {
            lock (_backupLock) _initialBackupDone = true;
        }
        // 最後の分をすぐ表に出す
        Dispatcher.InvokeAsync(() =>
        {
            UpdateStatus();
            if (_timer.IsEnabled) _ = PollAsync();
        });
    }

    // 再起動の後: logbackups に増えたファイル (初回の一覧にも、前に探したときにも無かったもの) を読み足す (裏のスレッド)。戻り値は増えたファイルの数
    private int ReadNewBackups(GameLogNames names)
    {
        var files = ListBackups();
        List<FileInfo> fresh;
        lock (_backupLock)
        {
            fresh = files.Where(f => _knownBackups.Add(f.FullName)).ToList();
            _backupTotal += fresh.Count;
        }
        if (fresh.Count > 0) ReadBackups(fresh, names, initial: false);
        return fresh.Count;
    }

    // logbackups の新しいファイルを 1 回探す (UI のスレッドから呼ぶ。探している間は次を始めない)
    private async Task RescanAsync()
    {
        if (_rescanRunning || _names == null) return;
        _rescanRunning = true;
        _rescanLeft--;
        try
        {
            var names = _names;
            var found = await Task.Run(() => ReadNewBackups(names));
            if (found > 0) _rescanLeft = 0;
        }
        catch { }   // 次の回にもう一度探す
        finally { _rescanRunning = false; }
        UpdateStatus();
    }

    // Game.log の増えた分を読み、変わっていれば表を作り直す (1 秒ごと)
    private async Task PollAsync()
    {
        if (_polling || _tail == null) return;
        _polling = true;
        try
        {
            var tail = _tail;
            var result = await Task.Run(() =>
            {
                var found = tail.Poll(out var reset);
                var parser = tail.Parser;
                var changed = reset || parser.Changed;
                parser.Changed = false;
                return (Found: found, Reset: reset, Changed: changed, Rows: changed ? parser.Entries.ToArray() : null);
            });
            _pollError = null;
            var missingChanged = _logMissing != !result.Found;
            _logMissing = !result.Found;
            // 再起動で読み直したときは、それまでの「現在」の行を捨てて新しい行だけにし、前回のログを logbackups から読み足す
            var changed = false;
            // Game.log の有無が変わったら、DB の自分の行を出すかどうかが変わる (GameLogStore.Merge の includeOwn) ので表を作り直す
            if (missingChanged && _storedRows.Length > 0) changed = true;
            if (result.Changed)
            {
                _currentRows = result.Rows ?? [];
                changed = true;
            }
            if (result.Reset)
            {
                _rescanLeft = RescanTries;
                // ゲーム起動 (Game.log が最初から読み直しになった) を知らせる (MainWindow の PC 間の同期が取り込みに使う)
                GameLogReset?.Invoke();
            }
            lock (_backupLock)
            {
                if (_backupChanged)
                {
                    _backupSnapshot = _backupRows.ToArray();
                    _backupChanged = false;
                    changed = true;
                }
            }
            // 名前待ちの行の書き換えで例外が出ても、この回の音・読み上げの判定は飛ばさない (例外は状態欄に出す)
            try
            {
                // ミッション名: どこかのファイルで名前が分かったら、名前待ちの行 (ID の先頭 8 文字) を書き換える
                var version = _missionNames.Version;
                if (changed || version != _missionVersionApplied)
                {
                    _missionVersionApplied = version;
                    if (_missionNames.Apply(_backupSnapshot.Concat(_currentRows))) changed = true;
                }
                // 場所の名前: 同じく、名前待ちの行 (「場所 ID n」) を書き換える
                var placeVersion = _placeNames.Version;
                if (changed || placeVersion != _placeVersionApplied)
                {
                    _placeVersionApplied = placeVersion;
                    if (_placeNames.Apply(_backupSnapshot.Concat(_currentRows))) changed = true;
                }
                // 船の名前: 同じく、船名待ちの呼び出しの行 (「船 ID n」「X 系（推定）」) を書き換える
                var vehicleVersion = _vehicleClasses.Version;
                if ((changed || vehicleVersion != _vehicleVersionApplied) && _names is { } names)
                {
                    _vehicleVersionApplied = vehicleVersion;
                    if (_vehicleClasses.Apply(_backupSnapshot.Concat(_currentRows), names.Ship)) changed = true;
                }
            }
            catch (Exception ex) { _pollError = ex.Message; }
            // 音・読み上げ: 名前待ちの行 (ミッション名・場所の名前・船の名前) を上で書き換えてから決める (読み上げる文に名前を入れるため)
            PlaySounds(result.Found, result.Reset, result.Changed ? _currentRows : null);
            if (changed) UpdateTable();
            else FlushUnsettled();
        }
        catch (Exception ex) { _pollError = ex.Message; }
        finally { _polling = false; }
        if (_rescanLeft > 0 && !_rescanRunning) _ = RescanAsync();
        UpdateStatus();
    }

    // 追い読みで新しく足された行から音を鳴らす (判定は GameLogSoundJudge)。鳴らせなかったときはログに残すだけ。
    // VOICEVOX で読み上げる設定なら、場面ごとの文 (GameLogVoiceScenes) を読み上げの順番待ち (GameLogVoicePlayer) に入れる
    // (読み上げられなければ、休戦ゾーン進入と到着だけ音のファイルを鳴らす)。読み上げない設定なら、休戦ゾーン進入と到着の音のファイルを今までどおり鳴らす
    private void PlaySounds(bool found, bool reset, IReadOnlyList<GameLogEntry>? rows)
    {
        try
        {
            var config = App.Config;
            var voice = config.GameLogVoiceVoxEnabled;
            if (voice && GameLogVoicePlayer.Log == null)
                GameLogVoicePlayer.Log = message => Dispatcher.BeginInvoke(() => ChatService.LogExternal(message));
            var sounds = _soundJudge.OnPoll(found, reset, rows, DateTime.UtcNow, config.ArmisticeSoundEnabled, config.ArrivalSoundEnabled,
                voice ? scene => GameLogVoiceScenes.IsEnabled(config, scene) : null);
            foreach (var ev in sounds)
            {
                string? path = ev.Kind switch
                {
                    GameLogSoundKind.Armistice => GameLogSoundPlayer.ResolvePath(config.ArmisticeSoundPath, GameLogSoundPlayer.DefaultArmisticeFile),
                    GameLogSoundKind.Arrival => GameLogSoundPlayer.ResolvePath(config.ArrivalSoundPath, GameLogSoundPlayer.DefaultArrivalFile),
                    _ => null,
                };
                if (voice)
                {
                    // 到着の {場所} は今の場所の名前 (保留から鳴らすときも、そのとき分かっている名前)
                    var place = ev.Kind == GameLogSoundKind.Arrival ? GameLogVoiceScenes.ArrivalPlace(ev.Entry, _placeNames) : null;
                    var text = GameLogVoiceScenes.BuildText(GameLogVoiceScenes.TemplateOf(config, ev.Scene), ev.Entry, place);
                    GameLogVoicePlayer.Enqueue(config.VoiceVoxUrl, GameLogVoiceScenes.SpeakerOf(config, ev.Scene), text, path);
                }
                else if (path != null && !GameLogSoundPlayer.Play(path, out var error)) ChatService.LogExternal($"[ゲームログ] {error}");
            }
        }
        catch (Exception ex)
        {
            ChatService.LogExternal($"[ゲームログ] 音を鳴らせませんでした: {ex.Message}");
        }
    }

    // 表に入れる (新しい順。同じ時刻は読んだ順の逆)。絞り込み・並べ替えは表の部品が残す。
    // 手元の行 (過去のログ + 今の Game.log) は［鉱石］などへ渡し (HandOverRecords)、確定済みのものを DB に残す (SaveSettled)。
    // 表には DB に残したほかの PC の行 (手元の Game.log が無ければ自分の行も) を合わせて出し (GameLogStore.Merge)、ボタンの状態はどちらの行にも決める
    private void UpdateTable()
    {
        var local = _backupSnapshot.Concat(_currentRows)
            .OrderByDescending(e => e.TimeUtc).ThenByDescending(e => e.Seq)
            .ToList();
        // 受け渡しはそれぞれ独立に呼ぶ (前は売買・採掘 → 納品 → 船の順に中から呼んでいて、途中で渡すものが無いと後ろが呼ばれなかった)
        HandOverRecords(local);
        HandOverDeliveries(local);
        HandOverShips(local);
        HandOverBlueprints(local);
        SaveSettled(local);
        var includeOwn = _tail == null || _logMissing;
        var entries = GameLogStore.Merge(local, _storedRows, OwnMachine, includeOwn);
        _storedShown = entries.Count - local.Count;
        ClassifyImports(entries);
        table.SetItems(entries.Cast<object>().ToList());
    }

    private void UpdateStatus()
    {
        var lines = new List<string>();
        if (_startError != null) lines.Add(_startError);
        if (_pollError != null) lines.Add($"Game.log を読めませんでした: {_pollError}");
        else if (_logMissing) lines.Add($"Game.log が見つかりません: {_logPath} (ゲームを起動すると作られます。設定タブのゲームのフォルダも確認してください)");
        else if (_tail != null) lines.Add($"Game.log: {_currentRows.Length} 件 (1 秒ごとに読み足します)");

        if (_tail != null)
        {
            int total, done, failed, count;
            bool initialDone;
            lock (_backupLock)
            {
                total = _backupTotal;
                done = _backupDone;
                failed = _backupFailed;
                count = _backupRows.Count;
                initialDone = _initialBackupDone;
            }
            string backup;
            if (_backupError != null && total == 0) backup = $"過去のログを読めませんでした: {_backupError}";
            else if (_backupDirMissing && total == 0) backup = $"過去のログのフォルダがありません: {_backupDir}";
            else if (!initialDone || done < total) backup = $"過去のログ: 読み込み中 {done} / {total} ファイル ({count} 件)";
            else backup = $"過去のログ: {total} ファイル ({count} 件)";
            if (failed > 0) backup += $"。読めなかったファイル {failed}";
            if (_rescanLeft > 0) backup += "。ゲームの再起動で移された前回のログを探しています";
            lines.Add(backup);
        }
        if (_storedShown > 0) lines.Add($"trade_cache.db に残した行 (ほかの PC の行): {_storedShown} 件を合わせて表示");
        ShowMessage(string.Join("\n", lines));
    }

    private void ShowMessage(string? text)
    {
        txtMessage.Text = text ?? "";
        txtMessage.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    // 船のクラス名・店の品目の内部名 → 表示名。引いた結果は覚えておく。裏の 2 つのスレッド (過去のログ・Game.log) から呼ばれるので lock する。
    //   船: ships.record_name = "EntityClassDefinition." + クラス名 の ships.name (@vehicle_Name…) を翻訳 DB で日本語にする。
    //       ships に無ければ item_index.record_name が同じ行の name_ja (無ければ name) (ships に入らない船: GLSN_Shiv 等)
    //   品目: items.record_name = "EntityClassDefinition." + 内部名 の items.name (@item_Name…) を翻訳 DB で日本語にする
    //   資源: resource_types.uuid = 商品売買の resourceGUID の name_key (@items_commodities_…) を翻訳 DB で日本語にする (無ければ name_en)
    //   訳: "@キー" をそのまま翻訳 DB で引く (鉱石の名前 items_commodities_…)
    //   船かどうか: クラス名が ships にあるか、item_index の Vehicle 系 (item_type に Vehicle を含む) にあるか
    //   BP: BP受信の通知の品名 → できる品の名前が一致する BP (BlueprintPanel.BlueprintsByOutputName。覚えるのは BlueprintPanel 側 (DB ファイルが変われば作り直す))
    //   BP受信の見出し: 翻訳 DB の crafting_hud_notification_received_blueprint (,P) の日本語・英語の %s の前 (GameLogParser.BlueprintHeadingFromTemplate)
    // 翻訳 DB の引き方は GameDataQueryService の LocReader.Text / ResolveItemNameJa と同じ
    // (key か key + ",P" の行。"!" で始まる訳・空の訳は英語。プレースホルダは使わない)。引けなければ内部名のまま。
    // DB は名前を引くたびに開いて閉じる (Pooling=False。開いたままだと設定の［DB クリア］で DB を消せない)
    private sealed class GameLogNames
    {
        private readonly object _lock = new();
        private readonly string _gamedataCacheDbPath;
        private readonly string _translationsDbPath;
        private readonly Dictionary<string, string> _ships = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _items = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> _resources = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string?> _translations = new(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _vehicleClasses = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _places = new(StringComparer.Ordinal);

        public GameLogNames(string gamedataCacheDbPath, string translationsDbPath)
        {
            _gamedataCacheDbPath = gamedataCacheDbPath;
            _translationsDbPath = translationsDbPath;
        }

        private static SqliteConnection? Open(string path)
        {
            if (!File.Exists(path)) return null;
            var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            // 開けなかったときの例外は Resolve まで上げる (その名前を覚えず、次の時にもう一度引く)
            try
            {
                c.Open();
                return c;
            }
            catch
            {
                c.Dispose();
                throw;
            }
        }

        // 覚えた名前を捨てる (訳・品目名が替わったとき、資源の種類 (resource_types) を取り出したとき。次に引くときに DB から引き直す)
        public void Clear()
        {
            lock (_lock)
            {
                _ships.Clear();
                _items.Clear();
                _itemLabels.Clear();
                _shipClassesByName.Clear();
                _resources.Clear();
                _translations.Clear();
                _vehicleClasses.Clear();
                _places.Clear();
                _blueprintHeadings = null;
            }
        }

        // BP受信の通知の品名 → 同定した BP (無ければ null)。DB を読めなければ例外 (GameLogParser が同定なしにする)
        public IReadOnlyList<GameLogBlueprint>? Blueprints(string name) => BlueprintPanel.BlueprintsByOutputName(_gamedataCacheDbPath, _translationsDbPath, name);

        // BP受信の通知の見出し (翻訳 DB の今の文言から作ったもの。日本語・英語のうち作れたもの)。読めなければ空 (覚えず、次の時にもう一度引く)
        public IReadOnlyList<string> BlueprintHeadings()
        {
            lock (_lock)
            {
                if (_blueprintHeadings != null) return _blueprintHeadings;
                var list = new List<string>();
                try
                {
                    var (en, ja) = Lookup("@crafting_hud_notification_received_blueprint");
                    foreach (var t in new[] { JapaneseText(ja), EnglishText(en) })
                        if (GameLogParser.BlueprintHeadingFromTemplate(t) is { } h && !list.Contains(h)) list.Add(h);
                }
                catch { return list; }
                if (list.Count > 0) _blueprintHeadings = list;   // 空 (翻訳 DB が無い・キーが無い) なら覚えず、次の時にもう一度引く
                return list;
            }
        }
        private List<string>? _blueprintHeadings;

        public string Ship(string className) => Resolve(className, _ships, ResolveShip);
        public string Item(string itemName) => Resolve(itemName, _items, name => ResolveName("items", name));
        // 船の表示名 (乗船の通知の「MISC Starfarer Gemini」= ships.name_en) → クラス名。同じ name_en の record (AI 用の派生など) のうち、
        // _Display 版を除いていちばん短い record_name のもの。無ければ null (引けなかったことも覚える)
        public string? ShipClassByName(string displayName)
        {
            if (string.IsNullOrEmpty(displayName)) return null;
            lock (_lock)
            {
                if (_shipClassesByName.TryGetValue(displayName, out var v)) return v;
                string? result = null;
                try
                {
                    using var game = Open(_gamedataCacheDbPath);
                    if (game == null) return null;
                    using var cmd = game.CreateCommand();
                    cmd.CommandText = "SELECT record_name FROM ships WHERE name_en = @n COLLATE NOCASE AND record_name NOT LIKE '%\\_Display%' ESCAPE '\\' ORDER BY length(record_name), record_name LIMIT 1";
                    cmd.Parameters.AddWithValue("@n", displayName.Trim());
                    if (cmd.ExecuteScalar() is string record && record.StartsWith("EntityClassDefinition.", StringComparison.Ordinal))
                        result = record["EntityClassDefinition.".Length..];
                }
                catch { return null; }   // DB を読めなかった (name_en 列の無い古い DB も) ときは覚えず、次の時にもう一度引く
                _shipClassesByName[displayName] = result;
                return result;
            }
        }
        private readonly Dictionary<string, string?> _shipClassesByName = new(StringComparer.Ordinal);
        // 品目の内部名 → 「日本語 (英語)」(貨物エレベーターで降ろした品)。items.name のキーを翻訳 DB で引いて Label にする。
        // items に無ければ item_index の name_ja (無ければ name)。引けなければ内部名のまま
        public string ItemLabel(string itemName) => Resolve(itemName, _itemLabels, ResolveItemLabel);
        private readonly Dictionary<string, string> _itemLabels = new(StringComparer.Ordinal);

        private string? ResolveItemLabel(string name)
        {
            string? key = null, indexName = null;
            using (var game = Open(_gamedataCacheDbPath))
            {
                if (game != null)
                {
                    using (var cmd = game.CreateCommand())
                    {
                        cmd.CommandText = "SELECT name FROM items WHERE record_name = @r LIMIT 1";
                        cmd.Parameters.AddWithValue("@r", "EntityClassDefinition." + name);
                        key = cmd.ExecuteScalar() as string;
                    }
                    if (key == null)
                    {
                        using var cmd = game.CreateCommand();
                        cmd.CommandText = "SELECT COALESCE(NULLIF(name_ja, ''), name) FROM item_index WHERE record_name = @r COLLATE NOCASE LIMIT 1";
                        cmd.Parameters.AddWithValue("@r", "EntityClassDefinition." + name);
                        indexName = cmd.ExecuteScalar() as string;
                    }
                }
            }
            if (key != null)
            {
                var (en, ja) = Lookup(key);
                var label = Label(JapaneseText(ja), EnglishText(en));
                if (label != null) return label;
            }
            return indexName != null ? Usable(indexName) : null;
        }
        // 場所の名前 (ログの Location[Stanton2_Orison] や ATC 名から取った Orison) → 翻訳 DB に同じキーがあれば日本語 (無ければ英語)。
        // キーで引けなければ、english が場所の名前と一致する行 (大文字小文字は区別しない) の japanese (ATC から取った Orison → Stanton2_Orison の ORISON)。
        // それも無ければそのまま。翻訳 DB のキーは場所の名前そのもの (Stanton2_Orison、RR_CRU_L1、Stanton1_Lorville)。
        // 無いもの (RR_CRU_LEO、Stanton2b_ArcCorpMining_Area141 等。全ログの 104 種のうち 79 種) はそのまま
        public string Place(string name) => Resolve(name, _places, n => Japanese("@" + n, ignoreCase: true) ?? JapaneseByEnglish(n));

        // english が text と一致する行 (大文字小文字は区別しない) の japanese (空・"!" 始まりなら english)。同じ english の行が複数あるときは
        // 使える japanese のある行を先に、同じならキーの順 (Orison: Stanton2_Orison の ORISON と ui_pregame_port_Orison_name の Orison → ORISON)。
        // english には索引が無いので全行を読む (引いた結果は Resolve が覚えるので、名前ごとに一度だけ)。無ければ null (_lock の中で呼ぶ)
        private string? JapaneseByEnglish(string text)
        {
            using var loc = Open(_translationsDbPath);
            if (loc == null) return null;
            using var cmd = loc.CreateCommand();
            cmd.CommandText = "SELECT english, japanese FROM translations WHERE english COLLATE NOCASE = @e " +
                              "ORDER BY (japanese IS NULL OR japanese = '' OR japanese LIKE '!%'), key LIMIT 1";
            cmd.Parameters.AddWithValue("@e", text);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            var en = r.IsDBNull(0) ? null : r.GetString(0);
            var ja = r.IsDBNull(1) ? null : r.GetString(1);
            return JapaneseText(ja) ?? EnglishText(en);
        }

        private string Resolve(string name, Dictionary<string, string> cache, Func<string, string?> lookup)
        {
            if (string.IsNullOrEmpty(name)) return name ?? "";
            lock (_lock)
            {
                if (cache.TryGetValue(name, out var v)) return v;
                string? result;
                try { result = lookup(name); }
                catch
                {
                    // DB を読めなかったときは覚えず、次の時にもう一度引く
                    return name;
                }
                result ??= name;
                cache[name] = result;
                return result;
            }
        }

        // ships / items の record_name = "EntityClassDefinition." + 名前 の name ("@キー") を翻訳 DB で日本語にする (_lock の中で呼ぶ)
        private string? ResolveName(string table, string name)
        {
            string? key = null;
            using (var game = Open(_gamedataCacheDbPath))
            {
                if (game != null)
                {
                    using var cmd = game.CreateCommand();
                    cmd.CommandText = $"SELECT name FROM {table} WHERE record_name = @r LIMIT 1";
                    cmd.Parameters.AddWithValue("@r", "EntityClassDefinition." + name);
                    key = cmd.ExecuteScalar() as string;
                }
            }
            return key != null ? Japanese(key) : null;
        }

        // 船: ships の name を翻訳 DB で日本語にする。ships に無ければ item_index の name_ja (無ければ name) (_lock の中で呼ぶ)
        private string? ResolveShip(string className)
        {
            string? key = null, indexName = null;
            using (var game = Open(_gamedataCacheDbPath))
            {
                if (game != null)
                {
                    using (var cmd = game.CreateCommand())
                    {
                        cmd.CommandText = "SELECT name FROM ships WHERE record_name = @r LIMIT 1";
                        cmd.Parameters.AddWithValue("@r", "EntityClassDefinition." + className);
                        key = cmd.ExecuteScalar() as string;
                    }
                    if (key == null)
                    {
                        using var cmd = game.CreateCommand();
                        cmd.CommandText = "SELECT COALESCE(NULLIF(name_ja, ''), name) FROM item_index WHERE record_name = @r LIMIT 1";
                        cmd.Parameters.AddWithValue("@r", "EntityClassDefinition." + className);
                        indexName = cmd.ExecuteScalar() as string;
                    }
                }
            }
            if (key != null) return Japanese(key);
            return indexName != null ? Usable(indexName) : null;
        }

        // "@キー" → 翻訳 DB の「日本語 (英語)」(Label)。訳が無ければ null (鉱石の名前 items_commodities_… に使う)。
        // 鉱石のキーは GameLogParser.MineralKey が小文字で組み立てるが、翻訳 DB のキーは大文字が混じるもの (items_commodities_Astatine 等) が
        // あるので、キーの大文字小文字を区別せずに引く
        public string? Translate(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            lock (_lock)
            {
                if (_translations.TryGetValue(key, out var v)) return v;
                string? result;
                try
                {
                    var (en, ja) = Lookup(key, ignoreCase: true);
                    result = Label(JapaneseText(ja), EnglishText(en));
                }
                catch { return null; }   // DB を読めなかったときは覚えず、次の時にもう一度引く
                _translations[key] = result;
                return result;
            }
        }

        // 資源の GUID (商品売買の resourceGUID = ResourceType の record の ID) → 「日本語 (英語)」。resource_types の name_key を翻訳 DB で
        // 日本語にし、英語は name_en (無ければ翻訳 DB の english)。日本語が無ければ英語だけ (Label)。表に無ければ null。
        // 表そのものが無い (古い DB) ・読めないときは覚えず null (起動後に裏で取り出したら引けるようになる)
        public string? Resource(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            lock (_lock)
            {
                if (_resources.TryGetValue(guid, out var v)) return v;
                string? result = null;
                try
                {
                    string? key = null, en = null;
                    bool found = false;
                    using (var game = Open(_gamedataCacheDbPath))
                    {
                        if (game == null) return null;
                        using var cmd = game.CreateCommand();
                        cmd.CommandText = "SELECT name_key, name_en FROM resource_types WHERE uuid = @u LIMIT 1";
                        cmd.Parameters.AddWithValue("@u", guid);
                        using var r = cmd.ExecuteReader();
                        if (r.Read())
                        {
                            found = true;
                            key = r.IsDBNull(0) ? null : r.GetString(0);
                            en = r.IsDBNull(1) ? null : r.GetString(1);
                        }
                    }
                    if (found)
                    {
                        var (locEn, locJa) = key != null ? Lookup(key) : default;
                        result = Label(JapaneseText(locJa), (en != null ? Usable(en) : null) ?? EnglishText(locEn));
                    }
                }
                catch { return null; }
                _resources[guid] = result;
                return result;
            }
        }

        // 「日本語 (英語)」の形。日本語と英語が同じ (大文字小文字は区別しない) か片方が無ければある方だけ。両方無ければ null
        private static string? Label(string? ja, string? en) =>
            ja != null && en != null && !string.Equals(ja, en, StringComparison.OrdinalIgnoreCase) ? $"{ja} ({en})" : ja ?? en;

        // 翻訳 DB の japanese → 表示に使う日本語 (空・"!" 始まり・使えない文字なら null)。Japanese と同じ見方
        private static string? JapaneseText(string? ja) => !string.IsNullOrWhiteSpace(ja) && !ja.StartsWith('!') ? Usable(ja) : null;
        // 翻訳 DB の english → 表示に使う英語 (空・使えない文字なら null)
        private static string? EnglishText(string? en) => !string.IsNullOrWhiteSpace(en) ? Usable(en) : null;

        // クラス名が船か: ships にあるか、item_index の Vehicle 系 (item_type に Vehicle を含む) にあるか。DB を読めなければ false (覚えない)
        public bool IsVehicleClass(string className)
        {
            if (string.IsNullOrEmpty(className)) return false;
            lock (_lock)
            {
                if (_vehicleClasses.TryGetValue(className, out var v)) return v;
                bool result;
                try
                {
                    using var game = Open(_gamedataCacheDbPath);
                    if (game == null) return false;
                    using var cmd = game.CreateCommand();
                    cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM ships WHERE record_name = @r) OR EXISTS (SELECT 1 FROM item_index WHERE record_name = @r AND item_type LIKE '%Vehicle%')";
                    cmd.Parameters.AddWithValue("@r", "EntityClassDefinition." + className);
                    result = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) != 0;
                }
                catch { return false; }
                _vehicleClasses[className] = result;
                return result;
            }
        }

        // ResolveItemNameJa と同じ: "@キー" を翻訳 DB の日本語 (無ければ英語) にする。キーでない・訳が無い・プレースホルダなら null。
        // ignoreCase: そのままのキーで見つからなければ、大文字小文字を区別せずに引き直す (key COLLATE NOCASE。translations の主キーの索引は
        // BINARY なので使われず全行を読む (写しの 91,062 行で 15ms ほど)。引いた結果は呼び出し元が覚えるので、キーごとに一度だけ)
        private string? Japanese(string value, bool ignoreCase = false)
        {
            var (en, ja) = Lookup(value, ignoreCase);
            string text;
            if (!string.IsNullOrWhiteSpace(ja) && !ja.StartsWith('!')) text = ja;
            else if (!string.IsNullOrWhiteSpace(en)) text = en;
            else return null;
            return Usable(text);
        }

        // "@キー" → 翻訳 DB の行の (english, japanese) をそのまま返す (無い列は null)。キーでない・行が無い・DB が無ければ (null, null)。
        // 引き方は Japanese のコメントのとおり
        private (string? En, string? Ja) Lookup(string value, bool ignoreCase = false)
        {
            if (!value.StartsWith('@')) return (null, null);
            string? en = null, ja = null;
            using (var loc = Open(_translationsDbPath))
            {
                if (loc == null) return (null, null);
                bool found = false;
                using (var cmd = loc.CreateCommand())
                {
                    cmd.CommandText = "SELECT english, japanese FROM translations WHERE key = @k OR key = @kp ORDER BY (key = @k) DESC LIMIT 1";
                    cmd.Parameters.AddWithValue("@k", value[1..]);
                    cmd.Parameters.AddWithValue("@kp", value[1..] + ",P");
                    using var r = cmd.ExecuteReader();
                    if (r.Read())
                    {
                        found = true;
                        en = r.IsDBNull(0) ? null : r.GetString(0);
                        ja = r.IsDBNull(1) ? null : r.GetString(1);
                    }
                }
                if (!found && ignoreCase)
                {
                    using var cmd = loc.CreateCommand();
                    cmd.CommandText = "SELECT english, japanese FROM translations WHERE key COLLATE NOCASE = @k OR key COLLATE NOCASE = @kp ORDER BY (key COLLATE NOCASE = @k) DESC LIMIT 1";
                    cmd.Parameters.AddWithValue("@k", value[1..]);
                    cmd.Parameters.AddWithValue("@kp", value[1..] + ",P");
                    using var r = cmd.ExecuteReader();
                    if (r.Read())
                    {
                        en = r.IsDBNull(0) ? null : r.GetString(0);
                        ja = r.IsDBNull(1) ? null : r.GetString(1);
                    }
                }
            }
            return (en, ja);
        }

        // 表示に使える文字か。空白を整え、キーのまま・プレースホルダなら null
        private static string? Usable(string text)
        {
            text = text.Replace('\u00A0', ' ').Trim();
            if (text.Length == 0 || text.StartsWith('@') || text.StartsWith("<=", StringComparison.Ordinal)
                || text.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase) || text.Contains("プレースホルダ")) return null;
            return text;
        }
    }
}
