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
// 船名・品名は gamedata_cache.db / translations.db (作業フォルダ。読み取り専用) で日本語にする (引けなければ内部名)
public partial class GameLogPanel : UserControl
{
    private const string CurrentSource = "現在";
    // 読み直し (ゲームの再起動) の後に logbackups の新しいファイルを探す回数。タイマーの 1 回 (1 秒) ごとに 1 回探す (最大で約 60 秒)。
    // 見つかったらやめる。ゲームが Game.log を移すのが少し遅れる場合に備える
    private const int RescanTries = 60;

    private bool _initialized;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _polling;

    private GameLogNames? _names;
    private GameLogTail? _tail;
    private readonly GameLogMissionNames _missionNames = new();
    private int _missionVersionApplied = -1;
    private string _logPath = "";
    private bool _logMissing;
    private string? _pollError;
    private GameLogEntry[] _currentRows = [];

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

    // 購入の行の「装備に入れる」ボタン (MainWindow が渡す)。
    //   PurchaseClassifier: 購入の一覧 → それぞれのボタンの状態 (HangarService.ClassifyPurchase)。null なら、どの行にもボタンを出さない
    //   PurchaseImporter:   購入 1 件を所持コンポーネントに入れる。入れたら (または入れ済みと分かったら) 表を作り直す
    public Func<IReadOnlyList<GameLogPurchase>, IReadOnlyList<PurchaseImportState>>? PurchaseClassifier { get; set; }
    public Action<GameLogPurchase>? PurchaseImporter { get; set; }

    public GameLogPanel()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => await PollAsync();
        Loaded += (_, _) =>
        {
            if (!_initialized)
            {
                _initialized = true;
                // 表の部品の例外でもアプリを落とさず、1 行の知らせにする
                try
                {
                    table.SetColumns(Columns());
                    if (!Start()) return;
                }
                catch (Exception ex) { ShowMessage($"読み込めませんでした: {ex.Message}"); return; }
            }
            if (_tail == null || _timer.IsEnabled) return;
            // 初回の表示で読み始め、以後はタブを表示していない間も 1 秒ごとに読む
            _timer.Start();
            _ = PollAsync();
        };
        // タイマーを止めるのはアプリの終了時だけ
        Dispatcher.ShutdownStarted += (_, _) => _timer.Stop();
    }

    private static string WorkDir()
    {
        var workDir = App.Config.WorkingDirectory;
        if (string.IsNullOrEmpty(workDir)) workDir = AppDomain.CurrentDomain.BaseDirectory;
        return workDir;
    }

    // 列: 日時 (ローカル時刻)、種類、内容、金額、ログ (どのファイル由来か。今の Game.log は「現在」)、装備 (購入の行の「装備に入れる」)
    private List<GridColumnSpec> Columns() =>
    [
        new()
        {
            Header = "日時", Filterable = false,
            Text = o => ((GameLogEntry)o).TimeUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss"),
            Key = o => ((GameLogEntry)o).TimeUtc,
        },
        new() { Header = "種類", Text = o => ((GameLogEntry)o).Kind, Key = o => ((GameLogEntry)o).Kind is { Length: > 0 } s ? s : null },
        new() { Header = "内容", Filterable = false, Text = o => ((GameLogEntry)o).Content, Key = o => ((GameLogEntry)o).Content is { Length: > 0 } s ? s : null },
        // 数値の列には絞り込みのボタン (Select) を出さない (並べ替えはできる)
        new()
        {
            Header = "金額", Numeric = true, Filterable = false,
            Text = o => ((GameLogEntry)o).Amount?.ToString("#,0.##") ?? "", Key = o => ((GameLogEntry)o).Amount,
        },
        new() { Header = "ログ", Text = o => ((GameLogEntry)o).Source, Key = o => ((GameLogEntry)o).Source is { Length: > 0 } s ? s : null },
        // 装備: 成功した購入のうち所持コンポーネントに入れられるものに「装備に入れる」(入れ済みなら押せない「入れ済み」)
        new()
        {
            Header = "装備",
            Text = o => ((GameLogEntry)o).ImportState switch { PurchaseImportState.Ready => "装備に入れる", PurchaseImportState.Imported => "入れ済み", _ => "" },
            Key = o => ((GameLogEntry)o).ImportState switch { PurchaseImportState.Ready => "装備に入れる", PurchaseImportState.Imported => "入れ済み", _ => null },
            Button = o => ((GameLogEntry)o).ImportState switch { PurchaseImportState.Ready => true, PurchaseImportState.Imported => false, _ => null },
            ButtonClick = o => { if (((GameLogEntry)o).Purchase is { } p) ImportPurchase(p); },
        },
    ];

    // 「装備に入れる」が押されたとき: MainWindow に入れてもらい、ボタンの状態を作り直す
    private void ImportPurchase(GameLogPurchase p)
    {
        PurchaseImporter?.Invoke(p);
        UpdateTable();
    }

    // 購入の行のボタンの状態を決める (PurchaseClassifier)。失敗したらボタンを出さない
    private void ClassifyPurchases(IReadOnlyList<GameLogEntry> entries)
    {
        var rows = entries.Where(e => e.Purchase != null).ToList();
        foreach (var e in rows) e.ImportState = PurchaseImportState.None;
        if (PurchaseClassifier == null || rows.Count == 0) return;
        try
        {
            var states = PurchaseClassifier(rows.Select(e => e.Purchase!).ToList());
            for (int i = 0; i < rows.Count && i < states.Count; i++) rows[i].ImportState = states[i];
        }
        catch { foreach (var e in rows) e.ImportState = PurchaseImportState.None; }
    }

    // 船名・品名の覚えた結果を捨てる。MainWindow から、訳・品目名 (item_index.name) が替わったときに呼ぶ。
    // これから読む行の名前は今の訳で引き直す (もう読んだ行の名前は読んだときのまま)
    public void ClearNameCache() => _names?.Clear();

    // 表を作り直す (ボタンの状態も決め直す)。MainWindow から、所持コンポーネントが変わったときなどに呼ぶ
    public void RefreshPurchaseButtons()
    {
        if (_tail == null) return;
        UpdateTable();
    }

    // 読み込みを始める。ゲームのフォルダが無ければ案内を出して false
    private bool Start()
    {
        var gamePath = App.Config.GamePath;
        if (string.IsNullOrEmpty(gamePath) || !Directory.Exists(gamePath))
        {
            ShowMessage("ゲームのフォルダが見つかりません (設定タブのゲームのフォルダを確認してください)");
            return false;
        }
        var workDir = WorkDir();
        _names = new GameLogNames(Path.Combine(workDir, "gamedata_cache.db"), Path.Combine(workDir, "translations.db"));
        _logPath = Path.Combine(gamePath, "Game.log");
        _tail = new GameLogTail(_logPath, CurrentSource, _names.Ship, _names.Item, _missionNames, _names.Translate, _names.IsVehicleClass, _names.Resource);

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
                    names.Translate, names.IsVehicleClass, names.Resource);
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
            _logMissing = !result.Found;
            // 再起動で読み直したときは、それまでの「現在」の行を捨てて新しい行だけにし、前回のログを logbackups から読み足す
            var changed = false;
            if (result.Changed)
            {
                _currentRows = result.Rows ?? [];
                changed = true;
            }
            if (result.Reset) _rescanLeft = RescanTries;
            lock (_backupLock)
            {
                if (_backupChanged)
                {
                    _backupSnapshot = _backupRows.ToArray();
                    _backupChanged = false;
                    changed = true;
                }
            }
            // ミッション名: どこかのファイルで名前が分かったら、名前待ちの行 (ID の先頭 8 文字) を書き換える
            var version = _missionNames.Version;
            if (changed || version != _missionVersionApplied)
            {
                _missionVersionApplied = version;
                if (_missionNames.Apply(_backupSnapshot.Concat(_currentRows))) changed = true;
            }
            if (changed) UpdateTable();
        }
        catch (Exception ex) { _pollError = ex.Message; }
        finally { _polling = false; }
        if (_rescanLeft > 0 && !_rescanRunning) _ = RescanAsync();
        UpdateStatus();
    }

    // 表に入れる (新しい順。同じ時刻は読んだ順の逆)。絞り込み・並べ替えは表の部品が残す
    private void UpdateTable()
    {
        var entries = _backupSnapshot.Concat(_currentRows)
            .OrderByDescending(e => e.TimeUtc).ThenByDescending(e => e.Seq)
            .ToList();
        ClassifyPurchases(entries);
        table.SetItems(entries.Cast<object>().ToList());
    }

    private void UpdateStatus()
    {
        var lines = new List<string>();
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
                _resources.Clear();
                _translations.Clear();
                _vehicleClasses.Clear();
            }
        }

        public string Ship(string className) => Resolve(className, _ships, ResolveShip);
        public string Item(string itemName) => Resolve(itemName, _items, name => ResolveName("items", name));

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
