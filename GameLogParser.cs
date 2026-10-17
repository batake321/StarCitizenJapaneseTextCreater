using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace StarCitizenJapaneseTextCreater;

// ［ゲームログ］タブの 1 行。TimeUtc はログの行頭の時刻 (UTC)。Source はどのログ由来か (現在の Game.log は「現在」、過去分はファイル名)。
// Kind・Content は後から分かったこと (ミッション名・船名) で書き換えることがある
public sealed class GameLogEntry
{
    public DateTime TimeUtc { get; init; }
    public string Kind { get; set; } = "";
    public string Content { get; set; } = "";
    public double? Amount { get; init; }
    public string Source { get; init; } = "";
    // 同じ時刻の行の並び (読んだ順。全ファイル通しの番号)
    public long Seq { get; init; }
    // ミッション名がまだ分からない契約終了の行の MissionId (内容は MissionId の先頭 8 文字)。名前を入れたら null
    public string? PendingMissionId { get; set; }
    // 場所の名前がまだ分からない到着・出発の行の着陸場所の ID (内容は「場所 ID n」で始まる)。名前を入れたら null
    public string? PendingPlaceId { get; set; }
    // 店の購入の行 (種類「購入」「購入失敗」) の依頼の中身。ほかの行は null
    public GameLogPurchase? Purchase { get; init; }
    // 所持・在庫に入れられる品 (購入・採掘・貨物エレベーターで降ろした品の行)。ほかの行は null。
    // 1 行に複数の品 (降ろした品) があるので一覧。印 (game_log_imports) は品ごとに付ける
    public List<GameLogImportItem>? ImportItems { get; init; }
    // ［ゲームログ］タブが決める「入れる」「取り消す」ボタンの状態 (UI のスレッドだけで使う)
    public PurchaseImportState ImportState { get; set; }
    // 商品売買の行 (種類「商品購入」「商品売却」「取引失敗」) の中身 (［鉱石］の commodity_trades に入れる)。ほかの行は null
    public GameLogTrade? Trade { get; init; }
    // 採掘の行の中身 (［鉱石］の mining_log に入れる)。ほかの行は null
    public GameLogMining? Mining { get; init; }
}

// ［ゲームログ］の行のボタンの状態。Ready = 「入れる」を押せる、Imported = 入れ済み (「取り消す」を押せる)、None = ボタン無し
public enum PurchaseImportState { None, Ready, Imported }

// 所持・在庫に入れられる品 1 つ (GameLogEntry.ImportItems)。
//   Kind: 印の種類 "purchase" (店の購入) | "mined" (採掘) | "dropped" (貨物エレベーターで降ろした)
//   LogKey: 印の鍵 (同じ出来事なら今の Game.log と過去のログで同じになる文字。purchase は GameLogPurchase.Key、
//           mined は 時刻・クラス名・個体 ID、dropped は 降ろした時刻・クラス名)
//   ItemClass: ログの中の品の内部名 (店の itemName / ItemClass / Harvestable_Mineral_1H_<X>)。item_index の record は "EntityClassDefinition." + これ
//   Quantity: 数。DisplayName: 内容の欄に出した品名 (ダブルクリックで品を見分けるのに使う)
public sealed class GameLogImportItem
{
    public string Kind { get; init; } = "";
    public string LogKey { get; init; } = "";
    public string ItemClass { get; init; } = "";
    public int Quantity { get; init; }
    public string DisplayName { get; init; } = "";

    public const string KindPurchase = "purchase";
    public const string KindMined = "mined";
    public const string KindDropped = "dropped";
    // 納品 (在庫から引く。鍵は 納品の時刻・MissionId・ObjectiveId、ItemClass は無し (品名は通知の日本語))
    public const string KindDelivered = "delivered";
    public const char KeySeparator = '\u001f';

    public static string MakeKey(params string[] parts) => string.Join(KeySeparator, parts);
}

// 商品売買の行の中身 (SendCommodityBuyRequest / SendCommoditySellRequest の行)。Kind は "buy" | "sell"。
// Result は '' が成功、失敗は後から来る RmToken_CommodityTransactionResponse の result の文字 (解析の途中で書き換わる)。
// Key は commodity_trades の主キーの組 (時刻・playerId・shopId・kioskId・kind・resourceGUID・quantity の文字)
public sealed class GameLogTrade
{
    public string LogTime { get; init; } = "";
    public string PlayerId { get; init; } = "";
    public string ShopId { get; init; } = "";
    public string KioskId { get; init; } = "";
    public string ShopName { get; init; } = "";
    public string Kind { get; init; } = "buy";
    public string ResourceUuid { get; init; } = "";
    public double? QuantityScu { get; init; }
    public string QuantityRaw { get; init; } = "";
    public double? TotalAuec { get; init; }
    public string TransactionMode { get; init; } = "";
    public string Result { get; set; } = "";

    public string Key => MakeKey(LogTime, PlayerId, ShopId, KioskId, Kind, ResourceUuid, QuantityRaw);

    public static string MakeKey(string logTime, string playerId, string shopId, string kioskId, string kind, string resourceUuid, string quantityRaw) =>
        string.Join(GameLogImportItem.KeySeparator, logTime, playerId, shopId, kioskId, kind, resourceUuid, quantityRaw);
}

// 採掘 (手掘り) の行の中身。Key は mining_log の主キーの組 (時刻・クラス名・個体 ID) で、印 (kind "mined") の鍵と同じ
public sealed class GameLogMining
{
    public string LogTime { get; init; } = "";
    public string PlayerName { get; init; } = "";
    public string MineralClass { get; init; } = "";
    public string EntityId { get; init; } = "";

    public string Key => MakeKey(LogTime, MineralClass, EntityId);

    public static string MakeKey(string logTime, string mineralClass, string entityId) =>
        string.Join(GameLogImportItem.KeySeparator, logTime, mineralClass, entityId);
}

// 店で買う依頼 (SendShopBuyRequest / SendStandardItemBuyRequest の行) の中身。Succeeded は結果 (RmShopFlowResponse) が Success だったとき true。
// LogTime は行頭の時刻の文字 (UTC。"2026-10-15T13:41:22.633")。Key は購入を見分ける組 (時刻・playerId・shopId・kioskId・itemName・quantity)。
// ゲームの再起動で同じ購入が今の Game.log と過去のログの両方に出ても、行が同じなら Key も同じ
public sealed class GameLogPurchase
{
    public DateTime TimeUtc { get; init; }
    public string LogTime { get; init; } = "";
    public string PlayerId { get; init; } = "";
    public string ShopId { get; init; } = "";
    public string KioskId { get; init; } = "";
    public string ItemName { get; init; } = "";
    public int Quantity { get; init; }
    public bool Succeeded { get; set; }

    public string Key => MakeKey(LogTime, PlayerId, ShopId, KioskId, ItemName, Quantity);

    public static string MakeKey(string logTime, string playerId, string shopId, string kioskId, string itemName, int quantity) =>
        string.Join("\u001f", logTime, playerId, shopId, kioskId, itemName, quantity.ToString(CultureInfo.InvariantCulture));

    // 「装備に入れる」ボタンを付ける購入の始まり: 2026-10-15 23:01 (日本時間) = 14:01 (UTC)。これより前の購入には付けない
    public static readonly DateTime ImportCutoffUtc = new(2026, 10, 15, 14, 1, 0, DateTimeKind.Utc);
}

// MissionId → ミッション名。［ゲームログ］タブ全体 (過去のログ・今の Game.log のすべての GameLogParser) で 1 つを共有し、ファイルをまたいで引く。
// 裏の複数のスレッドから書かれるので lock する。Version は名前を覚えるたびに増える (呼ぶ側が書き換えの要否を見る)
public sealed class GameLogMissionNames
{
    private readonly object _lock = new();
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private int _version;

    public int Version => System.Threading.Volatile.Read(ref _version);

    public void Set(string missionId, string name)
    {
        lock (_lock)
        {
            if (_names.TryGetValue(missionId, out var old) && old == name) return;
            _names[missionId] = name;
            System.Threading.Interlocked.Increment(ref _version);
        }
    }

    public bool TryGet(string missionId, out string name)
    {
        lock (_lock)
        {
            if (_names.TryGetValue(missionId, out var v)) { name = v; return true; }
        }
        name = "";
        return false;
    }

    // 名前待ちの行 (PendingMissionId あり) に、分かっている名前を入れる。入れた行があれば true
    public bool Apply(IEnumerable<GameLogEntry> entries)
    {
        var any = false;
        foreach (var e in entries)
        {
            if (e.PendingMissionId is not { } id || !TryGet(id, out var name)) continue;
            GameLogParser.ApplyMissionName(e, name);
            any = true;
        }
        return any;
    }
}

// 着陸場所の ID → 場所の名前 (<Update Inventory Location> の Landing の ID → <RequestLocationInventory> の Location[…])。
// GameLogMissionNames と同じ流儀で、［ゲームログ］タブ全体で 1 つを共有し、ファイルをまたいで引く
public sealed class GameLogPlaceNames
{
    private readonly object _lock = new();
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private int _version;

    public int Version => System.Threading.Volatile.Read(ref _version);

    public void Set(string placeId, string name)
    {
        lock (_lock)
        {
            if (_names.TryGetValue(placeId, out var old) && old == name) return;
            _names[placeId] = name;
            System.Threading.Interlocked.Increment(ref _version);
        }
    }

    public bool TryGet(string placeId, out string name)
    {
        lock (_lock)
        {
            if (_names.TryGetValue(placeId, out var v)) { name = v; return true; }
        }
        name = "";
        return false;
    }

    // 名前待ちの行 (PendingPlaceId あり) に、分かっている名前を入れる。入れた行があれば true
    public bool Apply(IEnumerable<GameLogEntry> entries)
    {
        var any = false;
        foreach (var e in entries)
        {
            if (e.PendingPlaceId is not { } id || !TryGet(id, out var name)) continue;
            GameLogParser.ApplyPlaceName(e, name);
            any = true;
        }
        return any;
    }
}

// Game.log の行を読んで、表に出す出来事 (GameLogEntry) にする。WPF には依存しない (コンソールからも試せる)。
// 1 ファイルにつき 1 つ作り、行を順に FeedLine に渡し、最後に Flush を呼ぶ。
// 名前の引き方 (船のクラス名 → 表示名、店の品目の内部名 → 表示名) は呼ぶ側が渡す。渡さなければ内部名のまま。
//
// ログの癖:
//   ・行頭の時刻は <2026-10-12T07:11:21.642Z> (UTC)
//   ・通知の文言などに改行があると、次の行が「同じ時刻 (または 1 ミリ秒だけ遅い時刻) + 空白 + 続き」([Notice] もタグも無い) になる。
//     続きの先頭 1 バイトが欠けていて先頭に置換文字 (U+FFFD) が出るので取り除き、1 行につなげる (欠けた字は戻せない)
//   ・つなぐのは通知 (Added notification) と船の呼び出し (SetVehicleSpawningInformations / SetVehicleSpawnedInformations) の行で、行末の [Team_…] が無いもの
public sealed class GameLogParser
{
    private static long _seq;

    private readonly string _source;
    private readonly Func<string, string> _shipName;
    private readonly Func<string, string> _itemName;
    // "@キー" → 翻訳 DB の訳 (無ければ null)。鉱石の名前 (items_commodities_…) に使う
    private readonly Func<string, string?> _translate;
    // クラス名が船 (ships か item_index の Vehicle 系) か。ナビゲーション・境界違反などの行から船の ID を覚えるときに、船でないもの (AImodule_ATC 等) を除くため
    private readonly Func<string, bool> _isVehicleClass;
    // 資源の GUID (商品売買の resourceGUID) → 名前 (無ければ null)
    private readonly Func<string, string?> _resourceName;
    // 場所の名前 (RequestLocationInventory の Location[…]、ATC 名から取った場所) → 表示名 (翻訳 DB に同じキーがあれば日本語。無ければそのまま)
    private readonly Func<string, string> _placeName;
    // 店の品目の内部名 → 「日本語 (英語)」(貨物エレベーターで降ろした品の名前。引けなければ内部名のまま)
    private readonly Func<string, string> _itemLabel;

    // 拾った行 (読んだ順)
    public List<GameLogEntry> Entries { get; } = new();
    // 行を足した・書き換えたら true。呼ぶ側が見て false に戻す
    public bool Changed { get; set; }

    // 続きの行を待っている行と、直前に読んだ行 (1 行目か続きの行) の行頭の時刻 ("<…Z>" とその値)
    private string? _pending;
    private string _pendingStamp = "";
    private DateTime _pendingTime;

    // MissionId → ミッション名 (受注・完了・失敗の通知から)
    private readonly Dictionary<string, string> _missionNames = new(StringComparer.OrdinalIgnoreCase);
    // ファイルをまたいで共有する MissionId → ミッション名 (渡されたときだけ)。渡されたら、名前待ちの行の書き換えは呼ぶ側が行う
    private readonly GameLogMissionNames? _sharedMissionNames;
    // 名前がまだ分からない契約終了の行 (MissionId → 行)。共有の対応が無いときだけ使う (このファイルの中で書き換える)
    private readonly Dictionary<string, List<GameLogEntry>> _missionWaiting = new(StringComparer.OrdinalIgnoreCase);

    // 船の ID → クラス名 (末尾の _ID を除いたもの)
    private readonly Dictionary<string, string> _vehicleClasses = new(StringComparer.Ordinal);
    // 自分の船の ID (自分が操縦した・乗っていて死んだ・衝突した・量子ジャンプした・呼び出した船と、自分の船だけが出る行 (OwnVehicleLearnTags) の船)。
    // 格納の行はこの ID のものだけ出す (周りの他人の船の格納も同じ行で出るため)
    private readonly HashSet<string> _ownVehicles = new(StringComparer.Ordinal);
    // 船名がまだ分からない呼び出しの行 (ID → (行, 内容の組み立て))。「船 ID …」で出しておき、分かったら書き換える
    private readonly Dictionary<string, List<(GameLogEntry Entry, Func<string, string> Build)>> _vehicleWaiting = new(StringComparer.Ordinal);
    // まだ出していない格納の行 (ID → (時刻, 種類, そのときの着陸場所の ID, 内容の組み立て))。格納は周りの着陸場で他人の船が格納されたときにも出るので、
    // 自分の船 (_ownVehicles) でクラス名も分かる (_vehicleClasses) まで表には出さず、分かったときに行にする
    private readonly Dictionary<string, List<(DateTime Time, string Kind, string PlaceId, Func<string, string> Build)>> _vehicleHidden = new(StringComparer.Ordinal);

    // 採掘 (手掘り): 拾った鉱石の候補 (個体 ID → (時刻, クラス名))。同じ個体 ID の Store で確定して行にする
    private readonly Dictionary<string, (DateTime Time, string Class)> _mineCandidates = new(StringComparer.Ordinal);
    // 所持品から取り出した鉱石のクラス名 → その時刻 (EquipItem / AttachItem の行から)。次の同じクラスの AttachmentReceived は拾得ではなく取り出し
    // (いちばん新しいものに結び付けて消す)。ただし EquipItem / AttachItem から PendingEquipWindow を過ぎた候補は取り出しとみなさず捨てる
    // (昨夜のログの実測では EquipItem から 2 秒ほどで AttachmentReceived が出る)
    private readonly Dictionary<string, DateTime> _pendingEquip = new(StringComparer.Ordinal);
    private static readonly TimeSpan PendingEquipWindow = TimeSpan.FromSeconds(10);
    // 取り出しとみなした個体 ID (同じ個体の AttachmentReceived が続けて 2 回出ることがある)
    private readonly HashSet<string> _takenOut = new(StringComparer.Ordinal);
    // 直前に確定した採掘の行 (直後の Update Container Items で所持数を添える)
    private (GameLogEntry Entry, string Class)? _lastMined;

    // 結果を待っている店の購入 (古い順。結果はいちばん新しいものに結び付ける。行そのものは Entries にある)
    private readonly List<GameLogEntry> _pendingPurchases = new();
    // 結果を待っている商品の取引 (type: Buying / Selling → 行)
    private readonly Dictionary<string, GameLogEntry> _pendingCommodity = new(StringComparer.OrdinalIgnoreCase);
    // 最後に選んだ量子ジャンプの行き先 (使っても消さない。そのファイルでまだ選んでいなければ null)
    private string? _quantumTarget;

    // 衝突をまとめるための直前の衝突 (時刻・船・行)
    private string _lastCollisionKey = "";
    private GameLogEntry? _lastCollision;
    private readonly List<string> _lastCollisionTargets = new();

    // 自分の名前 (<Update Inventory Location> Player [名前] / <RequestLocationInventory> Player[名前] / <Channel Created> nickname="名前")。分かるまで null
    private string? _playerName;
    // 自分の名前が分かるまで出せないチャンネル参加・退出の通知 (時刻, 船の表示名, 所有者, 参加なら true)
    private readonly List<(DateTime Time, string Ship, string Owner, bool Join)> _channelWaiting = new();
    // 今操縦している船の ID (ClearDriver で null)。null のときに最初に出た自分の船の航法行で「操縦」を出す
    private string? _drivingId;
    // 直前に操縦していた船の表示名 (到着・出発・ハンガー要求に添える)。分かるまで null
    private string? _lastShipName;
    // 今いる着陸場所の ID ("0" = 着陸場所の外)
    private string _landingId = "0";
    // いちばん最近いた着陸場所の ID (出ても覚えておく。呼び出し・格納の文の場所に使う。まだ無ければ "0")
    private string _placeId = "0";
    // 直前の出発の行 (同じ場所に ReentryWindow 以内に戻ったら取り消す)
    private GameLogEntry? _lastDeparture;
    private DateTime _lastDepartureTime;
    private string _lastDeparturePlace = "";
    // 着陸場所の ID → 場所の名前 (このファイルの中で分かったもの)
    private readonly Dictionary<string, string> _placeNames = new(StringComparer.Ordinal);
    // ファイルをまたいで共有する着陸場所の ID → 名前 (渡されたときだけ)。渡されたら、名前待ちの行の書き換えは呼ぶ側が行う
    private readonly GameLogPlaceNames? _sharedPlaceNames;
    // 名前がまだ分からない到着・出発の行 (ID → 行)。共有の対応が無いときだけ使う
    private readonly Dictionary<string, List<GameLogEntry>> _placeWaiting = new(StringComparer.Ordinal);
    // 直前の ATC (Connection Flow の communication partner ATC_DataManager…) とその時刻 (ハンガー要求に添える)
    private string? _lastAtc;
    private DateTime _lastAtcTime;

    // 同じ着陸場所を出てからこの時間以内に戻ったら、出入りの繰り返しとみなして出発の行を取り消す (最初の到着と最後の出発だけ残る)。
    // 昨夜のログの Area141 では出てから 110 秒・197 秒で戻っている
    private static readonly TimeSpan ReentryWindow = TimeSpan.FromSeconds(300);
    // ハンガー要求の直前とみなす ATC の行の時間 (昨夜のログでは 0.1 秒・5.8 秒前)
    private static readonly TimeSpan AtcWindow = TimeSpan.FromSeconds(60);

    public GameLogParser(string source, Func<string, string>? shipName = null, Func<string, string>? itemName = null,
        GameLogMissionNames? sharedMissionNames = null, Func<string, string?>? translate = null, Func<string, bool>? isVehicleClass = null,
        Func<string, string?>? resourceName = null, Func<string, string>? placeName = null, GameLogPlaceNames? sharedPlaceNames = null,
        Func<string, string>? itemLabel = null)
    {
        _source = source;
        _shipName = shipName ?? (s => s);
        _itemName = itemName ?? (s => s);
        _sharedMissionNames = sharedMissionNames;
        _translate = translate ?? (_ => null);
        _isVehicleClass = isVehicleClass ?? (_ => true);
        _resourceName = resourceName ?? (_ => null);
        _placeName = placeName ?? (s => s);
        _sharedPlaceNames = sharedPlaceNames;
        _itemLabel = itemLabel ?? _itemName;
    }

    // ─── 貨物エレベーター ───
    // 自分のキオスク操作 (Freight Inventory Grid Requesting Inventory) の時刻と場所の ID (着陸場所の ID と同じ値。"" = まだ無い)
    private DateTime _freightKioskTime = DateTime.MinValue;
    private string _freightLocationId = "";
    // 台に置いた品 (Type[Drop] の依頼)。依頼番号 → (クラス名, 個体 ID (Unstow で分かる。まだなら null), 置いた時刻)。
    // 拾い直した (個体 ID の右手 AttachmentReceived) ものは除く。下げたときにクラスごとに数えて行にし、空にする
    private readonly List<(int Request, string Class, string? Id, DateTime Time)> _platformItems = new();
    private DateTime _lastDropTime = DateTime.MinValue;
    // Unstow (床・台に出した) で分かった個体 ID の全部 (同じ個体の右手 AttachmentReceived は採掘の候補にしない)
    private readonly HashSet<string> _unstowed = new(StringComparer.Ordinal);
    // 下げの途中 (ClosingFrontGate / LoweringPlatform を見た) か。ClosedIdle で確定する
    private bool _freightLowering;
    // 呼び出し (FillUnstowRequest) の数と時刻 (直後の RaisingPlatform で行にする)
    private int _freightUnstowCount = -1;
    private DateTime _freightUnstowTime = DateTime.MinValue;
    // キオスク操作から Drop までの窓、最後の Drop から下げの完了までの窓、キオスク操作から下げの完了までの窓 (昨夜のログ: Drop はキオスク操作の 57 秒後から、
    // 下げの完了は最後の Drop の 45 秒後・キオスク操作の 13 秒後)、FillUnstowRequest から RaisingPlatform までの窓 (15 Oct のログで 4〜6 秒)
    private static readonly TimeSpan FreightDropWindow = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan FreightLowerWindow = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan FreightRaiseWindow = TimeSpan.FromSeconds(60);

    // 船の ID → クラス名を覚えるだけの行 (クラス_ID[ID] / 'クラス_ID' [ID] の形で船が出る)。船でないもの (AImodule_ATC_… や飲み物の瓶など) も
    // 同じ形で出るので、クラス名が船 (_isVehicleClass) のものだけ覚える。
    // 自分の船だけが出る行 (自分の操縦する船の経路計算・燃料計算。全ログ 330 本の実測で、ここに出た ID はすべて自分が操縦した船 (control token) の ID だった)。
    // 覚えた ID は「自分の船」として、格納の行を出す判断にも使う
    private static readonly string[] OwnVehicleLearnTags =
    [
        "<Calculate Route>",
        "<Failed to get starmap route data!>",
        "<Player Requested Fuel to Quantum Target",   // "- Local>" / "- Server Routing>" が続く
        "<Found Obstruction while Routing",           // ">" / " - Routing Around>" が続く
    ];
    // 周りの他人の船も出る行 (同じ実測で 9 割前後が自分の操縦した船でない ID)。名前 (ID → クラス名) は覚えるが「自分の船」とはみなさない
    private static readonly string[] OtherVehicleLearnTags =
    [
        "<Local Route Guard - Server Rerouted>",
        "<Host Vehicle Id SV Update>",
        "<Command Module Ship Spawned Paired>",
        "<Bad Spawn Position>",
        "<Boundary Violation>",
    ];
    private static readonly string[] VehicleLearnTags = [.. OwnVehicleLearnTags, .. OtherVehicleLearnTags];

    // 所持品・手に持つ物の行 (手掘りの鉱石を拾ったことを知るのに使う)。<InventoryManagement> / <InventoryManagementRequest> は
    // 2026-04 ごろのビルドのタグ名 (中身は今の <Add Inventory Management Move> / Queued Request と同じ形)
    private static readonly string[] InventoryTags =
    [
        "<AttachmentReceived>",
        "<Add Inventory Management Move>",
        "<Update Container Items Add New Item>",
        "<EquipItem>",
        "<InventoryManagement>",
        "<InventoryManagementRequest>",
        "<UnstowPendingEntities>",   // 所持品から床・台に出した個体 (Unstow Request[n] … finalized spawn of 'クラス_ID')
    ];

    // 貨物エレベーターの行
    private static readonly string[] FreightTags =
    [
        "<CSCLoadingPlatformManager::OnLoadingPlatformStateChanged>",            // 台の状態 (ClosingFrontGate → LoweringPlatform → ClosedIdle / RaisingPlatform)
        "<CEntityComponentFreightElevatorUIProvider::FillUnstowRequest>",        // 呼び出し (Entities: n, Location: L)
        "<CEntityComponentFreightElevatorUIProvider::Client_SendRequest>",       // 要求が断られた (Failed_FreightEntitiesMismatch)
    ];

    // 拾う行のタグ。これを含まない行は解析しない (過去ログは合計 600MB ほどあるので、先に string.Contains で絞る)
    private static readonly string[] Tags =
    [
        "<[ActorState] Dead>",
        "<FatalCollision>",
        "<SHUDEvent_OnNotification> Added notification",
        "<EndMission>",
        "<CEntityComponentShopUIProvider::SendShopBuyRequest>",
        "<CEntityComponentShoppingProvider::SendStandardItemBuyRequest>",
        "::RmShopFlowResponse>",
        "<CEntityComponentCommodityUIProvider::SendCommodityBuyRequest>",
        "<CEntityComponentCommodityUIProvider::SendCommoditySellRequest>",
        "<CEntityComponentCommodityUIProvider::RmToken_CommodityTransactionResponse>",
        "<Player Selected Quantum Target - Local>",
        "<Quantum Drive Arrived - Arrived at Final Destination>",
        "<CEntityComponentShipListProvider::SetVehicleSpawningInformations>",
        "<CEntityComponentShipListProvider::SetVehicleSpawnedInformations>",   // 呼び出しの完了 (船が着陸場に出た)
        "<LandingArea_UnregisterFromExternalSystems_StowingVehicle>",
        "<CLandingArea::UnregisterFromExternalSystems>",   // 2026-07-08 までのビルドの格納のタグ名 (文面は同じ)
        "<Vehicle Control Flow>",   // 操縦席を降りた (ClearDriver … releasing control token for 'クラス_ID' [ID])
        "<Update Inventory Location>",     // 着陸場所の出入り (到着・出発)。自分の名前もここから
        "<RequestLocationInventory>",      // 着陸場所の ID → 場所の名前
        "<Connection Flow>",               // ATC との通信 (ハンガー要求の場所)
        "<Channel Created>",               // nickname="自分の名前"
        "<CEntity::OnOwnerRemoved - entity attachment>",   // 自分の船の ID → クラス名を覚えるだけ (parent … name = クラス_ID)
        .. VehicleLearnTags,
        .. InventoryTags,
        .. FreightTags,
    ];

    public static bool HasTag(string line)
    {
        foreach (var t in Tags)
            if (line.Contains(t, StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool HasAnyTag(string line, string[] tags)
    {
        foreach (var t in tags)
            if (line.Contains(t, StringComparison.Ordinal)) return true;
        return false;
    }

    private static readonly Regex StampRegex = new(@"^<(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3})Z>", RegexOptions.Compiled);

    // 死亡: Actor '名前' [id] ejected from zone '乗っていた船_ID' [id] to zone '場所' [id] due to ...
    private static readonly Regex DeadRegex = new(@"<\[ActorState\] Dead> .*? ejected from zone '([^']*)' \[\d+\] to zone '([^']*)'", RegexOptions.Compiled);
    // 衝突: Fatal Collision occured for vehicle 船_ID [Part: …, Pos: …, Zone: 場所, PlayerPilot: 1] after hitting entity: 相手 [Zone: …
    private static readonly Regex CollisionRegex = new(@"<FatalCollision> Fatal Collision occured for vehicle (\S+) \[.*?Zone: ([^,\]]*), PlayerPilot: \d+\] after hitting entity: (.+?) \[Zone:", RegexOptions.Compiled);
    // 通知: Added notification "見出し: 本文" [番号] to queue. New queue size: n, MissionId: [guid], ObjectiveId: [...]
    private static readonly Regex NotificationRegex = new(@"<SHUDEvent_OnNotification> Added notification ""(.*)"" \[\d+\] to queue\.(?:.*?MissionId: \[([0-9A-Fa-f-]*)\])?(?:, ObjectiveId: \[([^\]]*)\])?", RegexOptions.Compiled);
    // 続きの行が来なかった通知 (行末の " [番号] to queue. が無い): 1 行目の文だけ (MissionId は無い)
    private static readonly Regex NotificationCutRegex = new(@"<SHUDEvent_OnNotification> Added notification ""(.*)$", RegexOptions.Compiled);
    // 契約の終了: Ending mission for player. MissionId[guid] ... CompletionType[Complete|Fail|Abandon] Reason[...]
    private static readonly Regex EndMissionRegex = new(@"<EndMission> .*?MissionId\[([0-9A-Fa-f-]+)\].*?CompletionType\[([^\]]*)\]", RegexOptions.Compiled);
    // 店で買う (2 種類の行): ... shopName[..] kioskId[..] client_price[42.000000] itemClassGUID[..] itemName[..] quantity[6]
    private static readonly Regex ShopBuyRegex = new(@"<CEntityComponent(?:ShopUIProvider::SendShopBuyRequest|ShoppingProvider::SendStandardItemBuyRequest)> .*? shopName\[([^\]]*)\].*? client_price\[([-0-9.]+)\].*? itemName\[([^\]]*)\] quantity\[(\d+)\]", RegexOptions.Compiled);
    // 店で買う依頼の playerId・shopId・kioskId (2 種類の行とも playerId[..] shopId[..] shopName[..] kioskId[..] の順)
    private static readonly Regex ShopBuyIdsRegex = new(@"playerId\[([^\]]*)\] shopId\[([^\]]*)\] shopName\[[^\]]*\] kioskId\[([^\]]*)\]", RegexOptions.Compiled);
    // 店の結果: ... result[Success] type[Buying] (ShopUIProvider) / Shop Flow Response - playerId[..] result[Success] (ShoppingProvider。type 無し)
    private static readonly Regex ShopResponseRegex = new(@"<CEntityComponent(?:ShopUIProvider|ShoppingProvider)::RmShopFlowResponse> .*? result\[([^\]]*)\](?: type\[([^\]]*)\])?", RegexOptions.Compiled);
    // 商品の買い: ... shopName[..] kioskId[..] price[2608320.000000] shopPricePerCentiSCU[..] resourceGUID[..] autoLoading[0] quantity[104000.000000 cSCU]
    private static readonly Regex CommodityBuyRegex = new(@"<CEntityComponentCommodityUIProvider::SendCommodityBuyRequest> .*? shopName\[([^\]]*)\].*? price\[([-0-9.]+)\].*? resourceGUID\[([^\]]*)\].*? quantity\[([-0-9.]+) cSCU\]", RegexOptions.Compiled);
    // 商品の売り: ... shopName[..] kioskId[..] amount[5060809.000000] resourceGUID[..] autoLoading[1] quantity[1136] transactionMode[...]
    private static readonly Regex CommoditySellRegex = new(@"<CEntityComponentCommodityUIProvider::SendCommoditySellRequest> .*? shopName\[([^\]]*)\].*? amount\[([-0-9.]+)\].*? resourceGUID\[([^\]]*)\].*? quantity\[([^\]]*)\]", RegexOptions.Compiled);
    // 商品の取引のエラー: Commodity Transaction Response Error - playerId[..] result[InsuffientStock] type[Selling]
    private static readonly Regex CommodityErrorRegex = new(@"<CEntityComponentCommodityUIProvider::RmToken_CommodityTransactionResponse> .*? result\[([^\]]*)\] type\[([^\]]*)\]", RegexOptions.Compiled);
    // 商品売買の playerId・shopId・kioskId (買い・売りとも playerId[..] shopId[..] shopName[..] kioskId[..] の順) と transactionMode[..] (売りだけ)
    private static readonly Regex CommodityIdsRegex = new(@"playerId\[([^\]]*)\] shopId\[([^\]]*)\] shopName\[[^\]]*\] kioskId\[([^\]]*)\]", RegexOptions.Compiled);
    private static readonly Regex TransactionModeRegex = new(@"transactionMode\[([^\]]*)\]", RegexOptions.Compiled);
    // ─── 貨物エレベーター ───
    // 自分のキオスク操作: <InventoryManagement> Freight Inventory Grid Requesting Inventory[ID:Location:場所の ID] for Player[名前]
    // (場所の ID は <Update Inventory Location> の Landing の ID と同じ値。直後の <RequestLocationInventory> で名前が分かる)
    private static readonly Regex FreightKioskRegex = new(@"<InventoryManagement> Freight Inventory Grid Requesting Inventory\[\d+:Location:(\d+)\] for Player\[([^\]]*)\]", RegexOptions.Compiled);
    // 台の状態: … [LoadingPlatformManager_FreightElevator_…] Platform state changed to 状態 (ShipElevator は見ない)
    private static readonly Regex FreightStateRegex = new(@"<CSCLoadingPlatformManager::OnLoadingPlatformStateChanged> .*?\[LoadingPlatformManager_FreightElevator_[^\]]*\] Platform state changed to (\w+)", RegexOptions.Compiled);
    // 呼び出し: … Processed bindings into transfer request - Entities: n, Location: L
    private static readonly Regex FreightUnstowRegex = new(@"<CEntityComponentFreightElevatorUIProvider::FillUnstowRequest> .*?Entities: (\d+), Location: (\d+)", RegexOptions.Compiled);
    // 断られた: … Error validating freight items. result[Failed_FreightEntitiesMismatch]
    private static readonly Regex FreightErrorRegex = new(@"<CEntityComponentFreightElevatorUIProvider::Client_SendRequest> .*?result\[([^\]]*)\]", RegexOptions.Compiled);
    // 台に置いた品: New Request[n] Player[名前] Type[Drop] … ItemClass[クラス] … Caller[…::DropItemFromContainerImpl] (1 行 1 個。
    // 旧タグ <InventoryManagement> New request[n] も同じ形)
    private static readonly Regex DropRegex = new(@"New [Rr]equest\[(\d+)\] Player\[[^\]]*\] Type\[Drop\] .*?ItemClass\[([^\]]+)\] .*?Caller\[[^\]]*::DropItemFromContainerImpl\]", RegexOptions.Compiled);
    // 床・台に出た個体: <UnstowPendingEntities> Unstow Request[n] for '名前' [id] finalized spawn of 'クラス_ID' [ID]
    private static readonly Regex UnstowRegex = new(@"<UnstowPendingEntities> Unstow Request\[(\d+)\] .*?finalized spawn of '([A-Za-z][A-Za-z0-9_]*?)_(\d{6,})' \[\3\]", RegexOptions.Compiled);
    // 納品の目標達成の本文: 品名 を n/n
    private static readonly Regex DeliveryRegex = new(@"^(.+?) を (\d+)/(\d+)$", RegexOptions.Compiled);
    // 量子ジャンプの行き先: Player has selected point XXX as their destination
    private static readonly Regex QuantumTargetRegex = new(@"<Player Selected Quantum Target - Local> .*?Player has selected point (.+?) as their destination", RegexOptions.Compiled);
    // 行の中の船: クラス_ID[ID] (量子ジャンプの行の | DRAK_Ironclad_819458464586[819458464586]|)
    private static readonly Regex VehicleRefRegex = new(@"([A-Za-z][A-Za-z0-9_]*?)_(\d{6,})\[\2\]", RegexOptions.Compiled);
    // 行の中の船 (引用符の形): Entity 'クラス_ID' [ID] (<Boundary Violation>)
    private static readonly Regex QuotedVehicleRefRegex = new(@"'([A-Za-z][A-Za-z0-9_]*?)_(\d{6,})' \[\2\]", RegexOptions.Compiled);
    // 船の呼び出し: VehicleEntityId: [id], LandingArea: 着陸場所 [Team_…]
    // (続きの行が来なかったときは行末まで)
    private static readonly Regex SpawnRegex = new(@"SetVehicleSpawningInformations - VehicleEntityId: \[(\d+)\], LandingArea: (.*?)\s*(?:\[Team_.*)?$", RegexOptions.Compiled);
    // 船の呼び出しの完了: SetVehicleSpawnedInformations - VehicleEntityId: [id], LandingATCId: [id], LandingArea: 着陸場所 [着陸場所の id] [Team_…]
    // (着陸場所の後ろの [id] は呼び出しの行には無いので、呼び出しと同じ見た目になるよう除く)
    private static readonly Regex SpawnedRegex = new(@"SetVehicleSpawnedInformations - VehicleEntityId: \[(\d+)\], LandingATCId: \[\d+\], LandingArea: (.*?)(?: \[\d+\])?\s*(?:\[Team_.*)?$", RegexOptions.Compiled);
    // 船の格納: [STOWING ON UNREGISTER] 着陸場所 [id] - Attempting to stow current vehicle [id] (新旧 2 つのタグ名)
    private static readonly Regex StowRegex = new(@"<(?:LandingArea_UnregisterFromExternalSystems_StowingVehicle|CLandingArea::UnregisterFromExternalSystems)> \[STOWING ON UNREGISTER\] (.*?) \[\d+\] - Attempting to stow current vehicle \[(\d+)\]", RegexOptions.Compiled);
    // 船の ID → クラス名: releasing control token for 'クラス_ID' [ID]
    private static readonly Regex ControlTokenRegex = new(@"releasing control token for '([A-Za-z0-9_]+?)_(\d+)' \[\2\]", RegexOptions.Compiled);
    // 末尾の _数字 (船のクラス名_ID)
    private static readonly Regex TrailingIdRegex = new(@"^(.+?)_(\d+)$", RegexOptions.Compiled);
    // 着陸場所の出入り: Player [名前] is changing location. Landing [前の ID] -> [今の ID]. Location [..] -> [..]. Pending [0]
    // (Landing の「前の ID」は着陸場所の外に出た後もその前の ID のまま出るので使わず、今の ID だけを見る)
    private static readonly Regex UpdateLocationRegex = new(@"<Update Inventory Location> Player \[([^\]]*)\] is changing location\. Landing \[(\d+)\] -> \[(\d+)\]", RegexOptions.Compiled);
    // 場所の所持品の要求: Player[名前] requested inventory for Location[Stanton2_Orison] (着陸場所にいる間に出たものを、その着陸場所の名前として覚える)
    private static readonly Regex RequestLocationRegex = new(@"<RequestLocationInventory> Player\[([^\]]*)\] requested inventory for Location\[([^\]]*)\]", RegexOptions.Compiled);
    // ATC との通信: … to track their communication partner ATC_DataManager_Port_Lawful-Orison [id] (AImodule_ATC_… は見ない)
    private static readonly Regex AtcPartnerRegex = new(@"<Connection Flow> .*?communication partner (ATC_DataManager\S*) \[\d+\]", RegexOptions.Compiled);
    // ATC 名 → 場所: ATC_DataManager[_Port][_Lawful|_SemiLawful|_Unlawful][-|_場所]。場所が無い・数字だけ (ATC_DataManager-001) なら ATC 名のまま
    private static readonly Regex AtcPlaceRegex = new(@"^ATC_DataManager(?:_Port)?(?:_(?:Semi|Un)?Lawful)?(?:[-_](.+))?$", RegexOptions.Compiled);
    // 自分の名前: <Channel Created> … nickname="名前"
    private static readonly Regex NicknameRegex = new(@"<Channel Created> .*?nickname=""([^""]*)""", RegexOptions.Compiled);
    // 自分の船の ID → クラス名: … to unblock removal of parent id = ID name = クラス_ID (UnattendedVehicleMarker を外す行。自分の船だけ)
    private static readonly Regex OwnerRemovedRegex = new(@"<CEntity::OnOwnerRemoved - entity attachment> .*?parent id = (\d+) name = ([A-Za-z][A-Za-z0-9_]*?)_\1(?:\s|$)", RegexOptions.Compiled);
    // 船のチャンネル (乗り降り) の通知: チャンネル「船の表示名 : 所有者」 に参加しました。/ を退出しました。
    private static readonly Regex ChannelRegex = new(@"^チャンネル「(.*)」 (に参加しました|を退出しました)", RegexOptions.Compiled);
    // RequestLocationInventory の Location[…] に出る、場所でない値
    private const string InvalidLocation = "INVALID_LOCATION_ID";

    // ─── 採掘 (手掘り) の行 ───
    // 手に持った: Player[名前] Attachment[Harvestable_Mineral_1H_鉱石_ID, Harvestable_Mineral_1H_鉱石, ID] Status[persistent] Port[weapon_attach_hand_right]
    // (所持品の中の鉱石の Attachment[Harvestable_Mineral_1H_鉱石, Default, 14043] Status[local] はこの形でないので拾わない。
    //  取り出しの途中で出る Port[inventory_pocket] 等の行も、手に持った Port[weapon_attach_hand_right] でないので拾わない)
    private static readonly Regex MineralAttachedRegex = new(@"<AttachmentReceived> .*?Attachment\[(Harvestable_Mineral_1H_[A-Za-z0-9_]+?)_(\d{6,}), \1, \2\] .*?Port\[weapon_attach_hand_right\]", RegexOptions.Compiled);
    // 所持品から取り出して手に持つ: <EquipItem> Request[n] equip from Inventory[…] Class[Harvestable_Mineral_1H_鉱石] Rank[…] … PostAction[Carry]
    private static readonly Regex MineralEquipRegex = new(@"<EquipItem> .*?Class\[(Harvestable_Mineral_1H_[^\]]+)\]", RegexOptions.Compiled);
    // 同上 (所持品の操作の行): Type[Interaction] … ItemClass[Harvestable_Mineral_1H_鉱石] … Caller[CSCLocalPlayerPersonalThoughtComponent::AttachItem]
    private static readonly Regex MineralAttachItemRegex = new(@"Type\[Interaction\] .*?ItemClass\[(Harvestable_Mineral_1H_[^\]]+)\] .*?Caller\[[^\]]*::AttachItem\]", RegexOptions.Compiled);
    // 所持品にしまう: Type[Store] … StoredEntity[Harvestable_Mineral_1H_鉱石_ID] (<Add Inventory Management Move> / 旧 <InventoryManagement>)、
    // Type[Store] … Item[Harvestable_Mineral_1H_鉱石_ID] (旧 <InventoryManagementRequest> Queued Request)
    private static readonly Regex MineralStoreRegex = new(@"Type\[Store\] .*?(?:StoredEntity|Item)\[(Harvestable_Mineral_1H_[A-Za-z0-9_]+?)_(\d{6,})\]", RegexOptions.Compiled);
    // しまった後の個数: Player '名前' [id] adding 1 stack(s) to N stack(s) of Entity Class[Harvestable_Mineral_1H_鉱石] Rank[…]
    private static readonly Regex ContainerAddRegex = new(@"<Update Container Items Add New Item> .*?adding \d+ stack\(s\) to (\d+) stack\(s\) of Entity Class\[([^\]]+)\]", RegexOptions.Compiled);
    private const string MineralClassPrefix = "Harvestable_Mineral_1H_";

    // 1 行を渡す (行末の \r は付いていてもよい)
    public void FeedLine(string line)
    {
        line = line.TrimEnd('\r', '\n');
        if (_pending != null)
        {
            // 直前の行の続き: 同じ時刻か 1 ミリ秒だけ遅い時刻 + 空白 + 本文の続き ([Notice] などの [ で始まらない)
            if (IsContinuation(line, out var head))
            {
                _pending += line[head..].TrimStart('\uFFFD');
                if (_pending.Contains("[Team_", StringComparison.Ordinal))
                {
                    var done = _pending;
                    _pending = null;
                    Process(done);
                }
                return;
            }
            // 続きが来なかった: そこまでの文で解析する
            var cut = _pending;
            _pending = null;
            Process(cut);
        }

        if (!HasTag(line)) return;

        if (NeedsContinuation(line))
        {
            var m = StampRegex.Match(line);
            if (m.Success && TryParseStamp(m.Groups[1].Value, out var t))
            {
                _pending = line;
                _pendingStamp = m.Value;
                _pendingTime = t;
                return;
            }
        }
        Process(line);
    }

    // line が待っている行の続きか。続きなら head = 本文の続きの始まりの位置。
    // 直前の行と同じ時刻か、1 ミリ秒だけ遅い時刻 (原本で 1 ミリ秒ずれた続きの行があった)。つないだら、その行の時刻を次に比べる相手にする
    private bool IsContinuation(string line, out int head)
    {
        head = 0;
        if (line.Length == 0 || line[0] != '<') return false;
        string stamp;
        DateTime time;
        if (line.StartsWith(_pendingStamp, StringComparison.Ordinal))
        {
            stamp = _pendingStamp;
            time = _pendingTime;
        }
        else
        {
            var m = StampRegex.Match(line);
            if (!m.Success || !TryParseStamp(m.Groups[1].Value, out time) || time - _pendingTime != TimeSpan.FromMilliseconds(1)) return false;
            stamp = m.Value;
        }
        head = stamp.Length + 1;
        if (line.Length <= head || line[stamp.Length] != ' ' || line[head] == '[') return false;
        _pendingStamp = stamp;
        _pendingTime = time;
        return true;
    }

    private static bool TryParseStamp(string text, out DateTime time) =>
        DateTime.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out time);

    // ファイルの終わり。続きを待っている行があれば、そこまでの文で解析する
    public void Flush()
    {
        if (_pending == null) return;
        var cut = _pending;
        _pending = null;
        Process(cut);
    }

    private static bool NeedsContinuation(string line) =>
        (line.Contains("<SHUDEvent_OnNotification> Added notification", StringComparison.Ordinal)
         || line.Contains("<CEntityComponentShipListProvider::SetVehicleSpawningInformations>", StringComparison.Ordinal)
         || line.Contains("<CEntityComponentShipListProvider::SetVehicleSpawnedInformations>", StringComparison.Ordinal))
        && !line.Contains("[Team_", StringComparison.Ordinal);

    private void Process(string line)
    {
        var sm = StampRegex.Match(line);
        if (!sm.Success) return;
        if (!TryParseStamp(sm.Groups[1].Value, out var time)) return;

        Match m;
        if (line.Contains("<Vehicle Control Flow>", StringComparison.Ordinal))
        {
            if ((m = ControlTokenRegex.Match(line)).Success) OnClearDriver(time, m.Groups[2].Value, m.Groups[1].Value);
            return;
        }
        if (line.Contains("<Update Inventory Location>", StringComparison.Ordinal))
        {
            if (!(m = UpdateLocationRegex.Match(line)).Success) return;
            LearnPlayerName(m.Groups[1].Value);
            OnLanding(time, m.Groups[3].Value);
            return;
        }
        if (line.Contains("<RequestLocationInventory>", StringComparison.Ordinal))
        {
            if (!(m = RequestLocationRegex.Match(line)).Success) return;
            LearnPlayerName(m.Groups[1].Value);
            // 着陸場所の外 (Landing 0) で出たもの (全ログ 2,920 件中 1,136 件) はどの場所か分からないので覚えない。
            // Location[INVALID_LOCATION_ID] は場所の名前ではないので覚えない
            if (_landingId != "0" && m.Groups[2].Value != InvalidLocation) LearnPlace(_landingId, m.Groups[2].Value);
            // 貨物エレベーターのキオスク操作の直後 (同じ秒) の行: その場所の ID の名前として覚える (着陸場所の外にいても)
            if (_freightLocationId.Length > 0 && m.Groups[2].Value != InvalidLocation && time - _freightKioskTime <= TimeSpan.FromSeconds(5))
                LearnPlace(_freightLocationId, m.Groups[2].Value);
            return;
        }
        if ((m = FreightKioskRegex.Match(line)).Success)
        {
            LearnPlayerName(m.Groups[2].Value);
            _freightKioskTime = time;
            _freightLocationId = m.Groups[1].Value;
            return;
        }
        if (HasAnyTag(line, FreightTags))
        {
            OnFreight(time, line);
            return;
        }
        if (line.Contains("<Connection Flow>", StringComparison.Ordinal))
        {
            if ((m = AtcPartnerRegex.Match(line)).Success)
            {
                _lastAtc = m.Groups[1].Value;
                _lastAtcTime = time;
            }
            return;
        }
        if (line.Contains("<Channel Created>", StringComparison.Ordinal))
        {
            if ((m = NicknameRegex.Match(line)).Success) LearnPlayerName(m.Groups[1].Value);
            return;
        }
        if (line.Contains("<CEntity::OnOwnerRemoved - entity attachment>", StringComparison.Ordinal))
        {
            if ((m = OwnerRemovedRegex.Match(line)).Success) LearnVehicleIfShip(m.Groups[1].Value, m.Groups[2].Value, own: true);
            return;
        }
        if (HasAnyTag(line, VehicleLearnTags))
        {
            var own = HasAnyTag(line, OwnVehicleLearnTags);
            LearnVehiclesInLine(line, own);
            if (own && _drivingId == null) OnPilot(time, line);
            return;
        }
        if (HasAnyTag(line, InventoryTags))
        {
            OnInventory(time, line);
            return;
        }
        if ((m = DeadRegex.Match(line)).Success)
        {
            var from = m.Groups[1].Value;
            LearnVehicleFromName(from);
            Add(time, "死亡", $"{ShipDisplay(from)} / 場所: {m.Groups[2].Value}");
            return;
        }
        if ((m = CollisionRegex.Match(line)).Success)
        {
            OnCollision(time, sm.Value, m.Groups[1].Value, m.Groups[2].Value.Trim(), m.Groups[3].Value.Trim());
            return;
        }
        if ((m = NotificationRegex.Match(line)).Success)
        {
            OnNotification(time, m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : "", m.Groups[3].Success ? m.Groups[3].Value : "");
            return;
        }
        if ((m = NotificationCutRegex.Match(line)).Success)
        {
            OnNotification(time, m.Groups[1].Value, "", "");
            return;
        }
        if ((m = EndMissionRegex.Match(line)).Success)
        {
            OnEndMission(time, m.Groups[1].Value, m.Groups[2].Value);
            return;
        }
        if ((m = ShopBuyRegex.Match(line)).Success)
        {
            var item = m.Groups[3].Value;
            var ids = ShopBuyIdsRegex.Match(line);
            var purchase = new GameLogPurchase
            {
                TimeUtc = time, LogTime = sm.Groups[1].Value, ItemName = item,
                // 数に直せない数量は 0 (HangarService.ClassifyPurchase が 0 以下の購入にはボタンを付けない)
                Quantity = int.TryParse(m.Groups[4].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var qty) ? qty : 0,
                PlayerId = ids.Success ? ids.Groups[1].Value : "",
                ShopId = ids.Success ? ids.Groups[2].Value : "",
                KioskId = ids.Success ? ids.Groups[3].Value : "",
            };
            var itemName = _itemName(item);
            var e = Add(time, "購入", $"{itemName} × {m.Groups[4].Value} / 店: {m.Groups[1].Value}", ParseNumber(m.Groups[2].Value), purchase,
                importItems: [new GameLogImportItem { Kind = GameLogImportItem.KindPurchase, LogKey = purchase.Key, ItemClass = item, Quantity = purchase.Quantity, DisplayName = itemName }]);
            _pendingPurchases.Add(e);
            return;
        }
        if ((m = ShopResponseRegex.Match(line)).Success)
        {
            // type があって Buying 以外 (売りなど) は購入の結果ではない
            if (m.Groups[2].Success && !m.Groups[2].Value.Equals("Buying", StringComparison.OrdinalIgnoreCase)) return;
            // いちばん新しい未決の依頼に結び付け、未決の一覧を空にする (応答の来ない依頼があっても、後の結果がずれないように)。
            // 依頼の行は依頼の時点で Entries に入れてあるので、一覧から外しても表の行は残る
            if (_pendingPurchases.Count == 0) return;
            var e = _pendingPurchases[^1];
            _pendingPurchases.Clear();
            var result = m.Groups[1].Value;
            if (!result.Equals("Success", StringComparison.OrdinalIgnoreCase))
            {
                e.Kind = "購入失敗";
                e.Content += $" / 結果: {result}";
                Changed = true;
            }
            else if (e.Purchase != null)
            {
                e.Purchase.Succeeded = true;
                Changed = true;
            }
            return;
        }
        if ((m = CommodityBuyRegex.Match(line)).Success)
        {
            // quantity は cSCU (1/100 SCU) なので SCU に直す。price は合計金額 (全ログ 53 件すべてで shopPricePerCentiSCU × quantity(cSCU) = price)
            var price = ParseNumber(m.Groups[2].Value);
            var scu = ParseNumber(m.Groups[4].Value) is double c ? c / 100 : (double?)null;
            var e = Add(time, "商品購入", $"{ResourceDisplay(m.Groups[3].Value)} {CommodityAmountText(scu, m.Groups[4].Value, price)} / 店: {m.Groups[1].Value}", price,
                trade: MakeTrade(sm.Groups[1].Value, line, m.Groups[1].Value, "buy", m.Groups[3].Value, scu, m.Groups[4].Value, price));
            _pendingCommodity["Buying"] = e;
            return;
        }
        if ((m = CommoditySellRegex.Match(line)).Success)
        {
            // 売りの quantity には単位が付かないが SCU (全ログ 238 件: 同じ資源の amount ÷ quantity が購入の price ÷ SCU と同じ桁で、
            // 行末の Cargo Box Data の boxSize × unitAmount と一致するか、それより大きい (箱の大きさが混ざった分))。amount は合計金額
            var amount = ParseNumber(m.Groups[2].Value);
            var e = Add(time, "商品売却", $"{ResourceDisplay(m.Groups[3].Value)} {CommodityAmountText(ParseNumber(m.Groups[4].Value), m.Groups[4].Value, amount)} / 店: {m.Groups[1].Value}", amount,
                trade: MakeTrade(sm.Groups[1].Value, line, m.Groups[1].Value, "sell", m.Groups[3].Value, ParseNumber(m.Groups[4].Value), m.Groups[4].Value, amount));
            _pendingCommodity["Selling"] = e;
            return;
        }
        if ((m = CommodityErrorRegex.Match(line)).Success)
        {
            var type = m.Groups[2].Value;
            if (!_pendingCommodity.TryGetValue(type, out var e)) return;
            _pendingCommodity.Remove(type);
            e.Kind = "取引失敗";
            e.Content += $" / 結果: {m.Groups[1].Value}";
            if (e.Trade != null) e.Trade.Result = m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : "Failed";
            Changed = true;
            return;
        }
        if ((m = QuantumTargetRegex.Match(line)).Success)
        {
            _quantumTarget = m.Groups[1].Value;
            if ((m = VehicleRefRegex.Match(line)).Success) LearnVehicle(m.Groups[2].Value, m.Groups[1].Value);
            return;
        }
        if (line.Contains("<Quantum Drive Arrived - Arrived at Final Destination>", StringComparison.Ordinal))
        {
            var ship = "";
            if ((m = VehicleRefRegex.Match(line)).Success)
            {
                LearnVehicle(m.Groups[2].Value, m.Groups[1].Value);
                ship = _shipName(m.Groups[1].Value);
            }
            // 行き先は最後に選んだもの。そのファイルでまだ選んでいなければ船名だけ
            var content = _quantumTarget == null ? ship : ship.Length > 0 ? $"{ship} → {_quantumTarget}" : _quantumTarget;
            Add(time, "量子ジャンプ", content);
            return;
        }
        // 呼び出し・呼び出し完了・格納は文の形。場所 (いちばん最近いた着陸場所) が分かれば「場所 の/で …」、分からなければ着陸場所 (LandingArea) を添える。
        // 場所の ID は分かるが名前がまだなら「場所 ID n」で出し、分かったら書き換える (AttachPlace)
        if ((m = SpawnRegex.Match(line)).Success)
        {
            var area = m.Groups[2].Value.Trim();
            var place = _placeId;
            AddWithVehicle(time, "呼び出し", m.Groups[1].Value, place, ship => place == "0"
                ? $"ハンガーエレベーターで {ship} を呼び出しました (着陸場所: {area})"
                : $"{PlaceDisplay(place)} のハンガーエレベーターで {ship} を呼び出しました");
            return;
        }
        if ((m = SpawnedRegex.Match(line)).Success)
        {
            // 呼び出しの完了 (同じ呼び出しで SetVehicleSpawningInformations の数秒後に出る)。呼び出しと同じく自分の船なので船名待ちで出す
            var area = m.Groups[2].Value.Trim();
            var place = _placeId;
            AddWithVehicle(time, "呼び出し完了", m.Groups[1].Value, place, ship => place == "0"
                ? $"ハンガーエレベーターで {ship} の呼び出しが完了しました (着陸場所: {area})"
                : $"{PlaceDisplay(place)} のハンガーエレベーターで {ship} の呼び出しが完了しました");
            return;
        }
        if ((m = StowRegex.Match(line)).Success)
        {
            var area = m.Groups[1].Value;
            var place = _placeId;
            AddWithVehicleIfKnown(time, "格納", m.Groups[2].Value, place, ship => place == "0"
                ? $"{ship} を格納しました (着陸場所: {area})"
                : $"{PlaceDisplay(place)} で {ship} を格納しました");
            return;
        }
    }

    // ─── 乗り降り・操縦・着陸場所 ───
    // 操縦席を降りた (ClearDriver)。次の自分の船の航法行で「操縦」を出せるようにする
    private void OnClearDriver(DateTime time, string id, string className)
    {
        LearnVehicle(id, className);
        var ship = _shipName(className);
        Add(time, "降りた", ship);
        _drivingId = null;
        _lastShipName = ship;
    }

    // ClearDriver の後 (またはファイルの最初) で最初に出た自分の船の航法行: 行の中の最初の船を「操縦」として出す。
    // 次の ClearDriver まで出さない (同じ船の航法行が続いても 1 回だけ)
    private void OnPilot(DateTime time, string line)
    {
        foreach (Match m in VehicleRefRegex.Matches(line))
        {
            if (!_isVehicleClass(m.Groups[1].Value)) continue;
            var ship = _shipName(m.Groups[1].Value);
            Add(time, "操縦", ship);
            _drivingId = m.Groups[2].Value;
            _lastShipName = ship;
            return;
        }
    }

    // 着陸場所の出入り (Update Inventory Location の Landing の今の ID)。0 以外へ = 到着、0 へ = 出発。
    // 同じ ID が続くとき (Landing [x] -> [x]) は、外にいた (_landingId が "0") なら到着、いたままなら何もしない
    private void OnLanding(DateTime time, string to)
    {
        if (to != "0")
        {
            if (to != _landingId) OnArrive(time, to);
        }
        else if (_landingId != "0") OnDepart(time);
    }

    private void OnArrive(DateTime time, string id)
    {
        _landingId = id;
        _placeId = id;
        // 同じ場所を出てから ReentryWindow 以内に戻った: 出入りの繰り返しなので、直前の出発の行を取り消し、到着の行も出さない
        if (_lastDeparture is { } d && _lastDeparturePlace == id && time - _lastDepartureTime <= ReentryWindow)
        {
            Entries.Remove(d);
            if (d.PendingPlaceId is { } pid && _placeWaiting.TryGetValue(pid, out var waiting)) waiting.Remove(d);
            Changed = true;
            _lastDeparture = null;
            return;
        }
        _lastDeparture = null;
        AttachPlace(Add(time, "到着", PlaceDisplay(id) + ShipSuffix()), id);
    }

    private void OnDepart(DateTime time)
    {
        var place = _landingId;
        _landingId = "0";
        var e = Add(time, "出発", PlaceDisplay(place) + ShipSuffix());
        AttachPlace(e, place);
        _lastDeparture = e;
        _lastDepartureTime = time;
        _lastDeparturePlace = place;
    }

    // 直前に操縦していた船 (分からなければ空)
    private string ShipSuffix() => _lastShipName is { } s ? $" / 船: {s}" : "";

    // 着陸場所の ID → 表示 (名前が分かっていれば名前、まだなら「場所 ID n」)
    private string PlaceDisplay(string id) => TryGetPlaceName(id, out var name) ? name : PlaceToken(id);

    private static string PlaceToken(string id) => $"場所 ID {id}";

    private bool TryGetPlaceName(string id, out string name)
    {
        if (_placeNames.TryGetValue(id, out var v))
        {
            name = v;
            return true;
        }
        if (_sharedPlaceNames != null && _sharedPlaceNames.TryGet(id, out name)) return true;
        name = "";
        return false;
    }

    // 名前がまだ分からない場所の行を名前待ちにする (ID が無い "0" や名前が分かっているものはそのまま)。共有の対応があるときは書き換えは呼ぶ側が行う
    private void AttachPlace(GameLogEntry e, string placeId)
    {
        if (placeId == "0" || TryGetPlaceName(placeId, out _)) return;
        e.PendingPlaceId = placeId;
        if (_sharedPlaceNames != null) return;
        if (!_placeWaiting.TryGetValue(placeId, out var list)) _placeWaiting[placeId] = list = new();
        list.Add(e);
    }

    // 着陸場所の ID → 名前を覚える (表示名にして覚える)。名前待ちの行があれば書き換える
    private void LearnPlace(string id, string rawName)
    {
        if (id.Length == 0 || rawName.Length == 0) return;
        var name = _placeName(rawName);
        _placeNames[id] = name;
        if (_sharedPlaceNames != null)
        {
            _sharedPlaceNames.Set(id, name);
            return;
        }
        if (!_placeWaiting.Remove(id, out var list)) return;
        foreach (var e in list) ApplyPlaceName(e, name);
        Changed = true;
    }

    // 名前待ちの行の「場所 ID n」を名前に置き換える (到着・出発は先頭、呼び出し・格納は文の中にある)
    public static void ApplyPlaceName(GameLogEntry e, string name)
    {
        if (e.PendingPlaceId is not { } id) return;
        e.Content = e.Content.Replace(PlaceToken(id), name, StringComparison.Ordinal);
        e.PendingPlaceId = null;
    }

    // ATC 名 → 場所 (ATC_DataManager_Port_Lawful-Orison → Orison、ATC_DataManager_Port_SemiLawful_ASD_Delving → ASD_Delving)。
    // 場所の部分が無い・数字だけ (ATC_DataManager-001、ATC_DataManager_Port_Lawful-001) なら ATC 名のまま
    private string AtcPlace(string atc)
    {
        var m = AtcPlaceRegex.Match(atc);
        if (!m.Success || !m.Groups[1].Success) return atc;
        var place = m.Groups[1].Value;
        foreach (var c in place)
            if (!char.IsDigit(c)) return _placeName(place);
        return atc;
    }

    // 自分の名前を覚える (最初に分かったもの)。名前が分かるまで保留していたチャンネル参加・退出の通知を出す
    private void LearnPlayerName(string name)
    {
        if (_playerName != null || name.Length == 0) return;
        _playerName = name;
        foreach (var (time, ship, owner, join) in _channelWaiting) AddChannel(time, ship, owner, join);
        _channelWaiting.Clear();
    }

    // 船のチャンネルの参加・退出の通知: 「船の表示名 : 所有者」。自分の名前が分かるまでは保留
    private void OnChannel(DateTime time, string body, bool join)
    {
        var sep = body.LastIndexOf(" : ", StringComparison.Ordinal);
        var ship = sep >= 0 ? body[..sep] : body;
        var owner = sep >= 0 ? body[(sep + 3)..] : "";
        if (_playerName == null)
        {
            _channelWaiting.Add((time, ship, owner, join));
            return;
        }
        AddChannel(time, ship, owner, join);
    }

    // 所有者が自分なら「乗船」「下船」(内容は船の表示名)、他人なら「同乗」「下船」(内容は「表示名 (所有者: X)」)
    private void AddChannel(DateTime time, string ship, string owner, bool join)
    {
        var own = owner == _playerName;
        var kind = join ? (own ? "乗船" : "同乗") : "下船";
        Add(time, kind, own ? ship : $"{ship} (所有者: {owner})");
    }

    // ─── 採掘 (手掘り) ───
    // 拾った鉱石: 手に持った (AttachmentReceived) 候補を個体 ID で覚え、同じ個体 ID をしまった (Type[Store]) ときに確定して 1 行にする。
    // 所持品から取り出して手に持ったときも同じ AttachmentReceived が出るので、直前 (PendingEquipWindow 以内) に同じクラスの EquipItem / AttachItem が
    // あれば取り出しとみなして出さない。しまった直後の Update Container Items (adding 1 stack(s) to N stack(s)) が来たら所持数 (N + 1) を添える
    private void OnInventory(DateTime time, string line)
    {
        var last = _lastMined;
        _lastMined = null;
        Match m;
        if ((m = UnstowRegex.Match(line)).Success)
        {
            // 床・台に出た個体 (取り出し・台に置いた品)。同じ個体を右手に持っても採掘の候補にしない。台に置いた品 (Drop の依頼) なら個体 ID を入れる
            var id = m.Groups[3].Value;
            _unstowed.Add(id);
            if (int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var req))
            {
                var i = _platformItems.FindIndex(p => p.Request == req && p.Id == null);
                if (i >= 0) _platformItems[i] = (req, _platformItems[i].Class, id, _platformItems[i].Time);
            }
            return;
        }
        if ((m = DropRegex.Match(line)).Success)
        {
            OnDrop(time, m.Groups[1].Value, m.Groups[2].Value);
            return;
        }
        if ((m = MineralAttachedRegex.Match(line)).Success)
        {
            var cls = m.Groups[1].Value;
            var id = m.Groups[2].Value;
            // 台に置いた品を拾い直した: 台から除く
            var dropped = _platformItems.FindLastIndex(p => p.Id == id);
            if (dropped >= 0) _platformItems.RemoveAt(dropped);
            if (_pendingEquip.Remove(cls, out var equipTime))
            {
                // 窓を過ぎた候補は捨てる (取り出しとみなさず、拾得の候補として扱う)
                if (time - equipTime <= PendingEquipWindow)
                {
                    _takenOut.Add(id);
                    return;
                }
            }
            if (_takenOut.Contains(id)) return;
            // Unstow で床・台に出た個体 (エレベーターの台から拾い直したものなど) は採掘ではない。本物の採掘の個体 ID は事前に Unstow に出ない
            if (_unstowed.Contains(id)) return;
            _mineCandidates[id] = (time, cls);
            return;
        }
        if ((m = MineralEquipRegex.Match(line)).Success || (m = MineralAttachItemRegex.Match(line)).Success)
        {
            _pendingEquip[m.Groups[1].Value] = time;
            return;
        }
        if ((m = MineralStoreRegex.Match(line)).Success)
        {
            var id = m.Groups[2].Value;
            if (!_mineCandidates.Remove(id, out var c)) return;
            var logTime = LogTimeText(c.Time);
            var mining = new GameLogMining { LogTime = logTime, PlayerName = _playerName ?? "", MineralClass = c.Class, EntityId = id };
            var name = MineralName(c.Class);
            var e = Add(c.Time, "採掘", $"{name} ×1", mining: mining,
                importItems: [new GameLogImportItem { Kind = GameLogImportItem.KindMined, LogKey = mining.Key, ItemClass = c.Class, Quantity = 1, DisplayName = name }]);
            _lastMined = (e, c.Class);
            return;
        }
        if ((m = ContainerAddRegex.Match(line)).Success && last is { } lm && lm.Class == m.Groups[2].Value
            && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var to))
        {
            lm.Entry.Content += $"（所持 {to + 1}）";
            Changed = true;
        }
    }

    // 鉱石のクラス名 Harvestable_Mineral_1H_<X> → 名前。翻訳 DB の items_commodities_<x> (無ければ <X> のまま)
    private string MineralName(string cls)
    {
        var x = cls.StartsWith(MineralClassPrefix, StringComparison.Ordinal) ? cls[MineralClassPrefix.Length..] : cls;
        return _translate("@items_commodities_" + MineralKey(x)) ?? x;
    }

    // <X> → 翻訳キーの末尾。大文字で語を区切り、小文字 + "_" でつなぐ (CarinitePure → carinite_pure、Aphorite → aphorite)。
    // 元から "_" があればそのまま、大文字が続くところ (RMC など) は区切らない
    internal static string MineralKey(string x)
    {
        var sb = new StringBuilder(x.Length + 4);
        for (int i = 0; i < x.Length; i++)
        {
            var c = x[i];
            if (char.IsUpper(c) && i > 0 && (char.IsLower(x[i - 1]) || char.IsDigit(x[i - 1]))) sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    // 商品売買の資源の GUID → 名前。引けなければ今までどおり「品目 先頭 8 文字」
    private string ResourceDisplay(string guid) => _resourceName(guid) ?? $"品目 {ShortGuid(guid)}";

    // 商品の量と単価: 「1,728 SCU (単価 1,162 aUEC/SCU)」。合計金額は金額の列に出すのでここには入れない。量が数に直せなければログの文字のまま。
    // 単価は合計 ÷ 量を整数に丸める (四捨五入)。合計が 0 以下 (失敗した取引の amount[0]) か量が 0 以下なら量だけ
    private static string CommodityAmountText(double? scu, string rawQuantity, double? total)
    {
        var text = $"{(scu is double q ? q.ToString("#,0.##", CultureInfo.InvariantCulture) : rawQuantity)} SCU";
        if (total is double t && t > 0 && scu is double n && n > 0)
            text += $" (単価 {Math.Round(t / n, MidpointRounding.AwayFromZero).ToString("#,0", CultureInfo.InvariantCulture)} aUEC/SCU)";
        return text;
    }

    // 商品売買の行の中身 (［鉱石］の commodity_trades へ)。playerId・shopId・kioskId は行の中から、transactionMode は売りの行だけにある
    private static GameLogTrade MakeTrade(string logTime, string line, string shopName, string kind, string resourceGuid, double? scu, string rawQuantity, double? total)
    {
        var ids = CommodityIdsRegex.Match(line);
        var mode = TransactionModeRegex.Match(line);
        return new GameLogTrade
        {
            LogTime = logTime, ShopName = shopName, Kind = kind, ResourceUuid = resourceGuid, QuantityScu = scu, QuantityRaw = rawQuantity, TotalAuec = total,
            PlayerId = ids.Success ? ids.Groups[1].Value : "",
            ShopId = ids.Success ? ids.Groups[2].Value : "",
            KioskId = ids.Success ? ids.Groups[3].Value : "",
            TransactionMode = mode.Success ? mode.Groups[1].Value : "",
        };
    }

    // 行頭の時刻の文字 (UTC。"2026-10-15T13:41:22.633")。印・記録の鍵に使う
    private static string LogTimeText(DateTime time) => time.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);

    // ─── 貨物エレベーター ───
    // 台に置いた品 (Type[Drop])。自分のキオスク操作から FreightDropWindow 以内のものだけ数える (個体 ID は後の Unstow で入る)
    private void OnDrop(DateTime time, string request, string itemClass)
    {
        if (_freightLocationId.Length == 0 || time - _freightKioskTime > FreightDropWindow) return;
        if (!int.TryParse(request, NumberStyles.None, CultureInfo.InvariantCulture, out var req)) return;
        _platformItems.Add((req, itemClass, null, time));
        _lastDropTime = time;
    }

    // 台の状態・呼び出し・断られた
    private void OnFreight(DateTime time, string line)
    {
        Match m;
        if ((m = FreightStateRegex.Match(line)).Success)
        {
            var state = m.Groups[1].Value;
            // 下げ: 自分のキオスク操作から FreightLowerWindow 以内の ClosingFrontGate / LoweringPlatform → ClosedIdle (他人の操作でも同じ行が出るので窓で絞る)
            if (state is "ClosingFrontGate" or "LoweringPlatform")
            {
                if (_freightLocationId.Length > 0 && time - _freightKioskTime <= FreightLowerWindow) _freightLowering = true;
                return;
            }
            if (state == "ClosedIdle")
            {
                if (!_freightLowering) return;
                _freightLowering = false;
                if (time - _freightKioskTime > FreightLowerWindow) return;
                OnFreightLowered(time);
                return;
            }
            if (state == "RaisingPlatform")
            {
                if (_freightUnstowCount < 0 || time - _freightUnstowTime > FreightRaiseWindow) return;
                var n = _freightUnstowCount;
                var t = _freightUnstowTime;
                _freightUnstowCount = -1;
                if (n <= 0) return;   // 品の無い呼び出し (Entities: 0) は行にしない
                var place = _freightLocationId;
                AttachPlace(Add(t, "貨物エレベーター", $"{PlaceDisplay(place)} の貨物エレベーターで {n} 品を呼び出しました"), place);
                return;
            }
            return;
        }
        if ((m = FreightUnstowRegex.Match(line)).Success)
        {
            // 呼び出し (品名は出ない)。場所の ID はキオスク操作のものと同じ値なので、ここで覚え直す
            _freightUnstowCount = int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
            _freightUnstowTime = time;
            if (_freightLocationId.Length == 0) _freightLocationId = m.Groups[2].Value;
            return;
        }
        if ((m = FreightErrorRegex.Match(line)).Success)
        {
            Add(time, "貨物エレベーター", "貨物エレベーターの要求が断られました");
            return;
        }
    }

    // 下げが完了した: 台に置いた品 (最後の Drop から FreightLowerWindow 以内) をクラスごとに数えて 1 行にする。
    // 「<場所> の貨物エレベーターで <品名 ×n、…> を降ろしました」(品名は「日本語 (英語)」。並びは最初に置いた順)。品が無ければ行にしない
    private void OnFreightLowered(DateTime time)
    {
        if (_platformItems.Count == 0) return;
        if (time - _lastDropTime > FreightLowerWindow)
        {
            _platformItems.Clear();
            return;
        }
        var counts = new List<(string Class, int Count)>();
        foreach (var p in _platformItems)
        {
            var i = counts.FindIndex(c => c.Class == p.Class);
            if (i >= 0) counts[i] = (p.Class, counts[i].Count + 1);
            else counts.Add((p.Class, 1));
        }
        _platformItems.Clear();
        var logTime = LogTimeText(time);
        var items = new List<GameLogImportItem>(counts.Count);
        var names = new List<string>(counts.Count);
        foreach (var (cls, count) in counts)
        {
            var name = cls.StartsWith(MineralClassPrefix, StringComparison.Ordinal) ? MineralName(cls) : _itemLabel(cls);
            names.Add($"{name} ×{count}");
            items.Add(new GameLogImportItem
            {
                Kind = GameLogImportItem.KindDropped, LogKey = GameLogImportItem.MakeKey(logTime, cls), ItemClass = cls, Quantity = count, DisplayName = name,
            });
        }
        var place = _freightLocationId;
        var e = Add(time, "貨物エレベーター", $"{PlaceDisplay(place)} の貨物エレベーターで {string.Join("、", names)} を降ろしました", importItems: items);
        AttachPlace(e, place);
    }

    // ─── 衝突 ───
    // 同じ時刻・同じ船の行が部位ごとに複数出るので 1 件にまとめる (相手が違えば並べる)
    private void OnCollision(DateTime time, string stamp, string vehicle, string zone, string target)
    {
        LearnVehicleFromName(vehicle);
        var key = stamp + "\u0001" + vehicle;
        if (_lastCollision != null && key == _lastCollisionKey)
        {
            if (!_lastCollisionTargets.Contains(target))
            {
                _lastCollisionTargets.Add(target);
                _lastCollision.Content = CollisionText(vehicle, zone, _lastCollisionTargets);
                Changed = true;
            }
            return;
        }
        _lastCollisionKey = key;
        _lastCollisionTargets.Clear();
        _lastCollisionTargets.Add(target);
        _lastCollision = Add(time, "衝突", CollisionText(vehicle, zone, _lastCollisionTargets));
    }

    private string CollisionText(string vehicle, string zone, List<string> targets) =>
        $"{ShipDisplay(vehicle)} → 相手: {string.Join(", ", targets)} / 場所: {zone}";

    // ─── 通知 ───
    private static readonly string[] AcceptHeadings = ["コントラクト受諾", "契約が成立しました"];
    private static readonly string[] CompleteHeadings = ["コントラクト完了", "契約完了"];
    private static readonly string[] FailHeadings = ["コントラクト失敗", "契約に失敗しました"];
    private static readonly (string Heading, string Kind)[] ObjectiveHeadings =
    [
        ("目標達成", "目標達成"),
        ("目標失敗", "目標失敗"),
        ("目標撤回", "目標撤回"),
        ("目標中断", "目標撤回"),
    ];
    private static readonly string[] AreaHeadings = ["休戦ゾーンに進入 - 戦闘禁止", "休戦ゾーンを突破 - 注意せよ", "立入禁止区域から退出"];
    // 区域 (見出しの前方一致)。logbackups での実際の文は「制限区域から離脱中: 」「私有地に進入: 」「私有地を突破: 」(本文は空)
    private static readonly string[] AreaPrefixHeadings = ["制限区域から離脱中", "私有地に進入", "私有地を突破"];

    private void OnNotification(DateTime time, string text, string missionId, string objectiveId)
    {
        if (missionId.Trim('0', '-').Length == 0) missionId = "";   // 00000000-0000-… はミッション無し

        // 船のチャンネルの参加・退出 (乗り降り)。見出しの ": " で切る前に見る (「表示名 : 所有者」の " : " で切れてしまうため)
        Match cm;
        if ((cm = ChannelRegex.Match(text)).Success)
        {
            OnChannel(time, cm.Groups[1].Value, cm.Groups[2].Value == "に参加しました");
            return;
        }
        // ハンガー要求 (離陸前と着陸時の両方に出る): 直前 (AtcWindow 以内) の ATC の場所と、直前に操縦していた船
        if (text.StartsWith("ハンガーリクエスト完了", StringComparison.Ordinal))
        {
            var parts = new List<string>(2);
            if (_lastAtc != null && time - _lastAtcTime <= AtcWindow) parts.Add(AtcPlace(_lastAtc));
            if (_lastShipName != null) parts.Add($"船: {_lastShipName}");
            Add(time, "ハンガー要求", string.Join(" / ", parts));
            return;
        }

        if (StartsWithAny(text, AcceptHeadings, out var rest))
        {
            var name = Body(rest);
            LearnMission(missionId, name);
            Add(time, "契約受注", name.Length > 0 ? name : MissionShort(missionId));
            return;
        }
        // 完了・失敗の通知は EndMission と重なるので行にはせず、ミッション名を覚えるだけ
        if (StartsWithAny(text, CompleteHeadings, out rest) || StartsWithAny(text, FailHeadings, out rest))
        {
            LearnMission(missionId, Body(rest));
            return;
        }
        foreach (var (heading, kind) in ObjectiveHeadings)
        {
            if (!text.StartsWith(heading, StringComparison.Ordinal)) continue;
            var body = Body(text[heading.Length..]);
            Add(time, kind, body);
            // 納品: 目標達成「品名 を n/n」(両数が同じ) で ObjectiveId が dropoff_… のもの。目標達成の行はそのまま残し、納品の行を足す
            if (kind == "目標達成" && objectiveId.StartsWith("dropoff_", StringComparison.OrdinalIgnoreCase)) OnDelivery(time, body, missionId, objectiveId);
            return;
        }
        // 区域: 見出し (": " の前) そのもの
        var colon = text.IndexOf(": ", StringComparison.Ordinal);
        var title = (colon >= 0 ? text[..colon] : text).Trim().TrimEnd(':').Trim();
        if (title.EndsWith("管轄区域に進入", StringComparison.Ordinal) || Array.IndexOf(AreaHeadings, title) >= 0
            || StartsWithAny(title, AreaPrefixHeadings, out _))
            Add(time, "区域", title);
    }

    // 通知の文に入る書式のタグ (<EM4>…</EM4> 等)。納品の行のミッション名からは外す
    private static readonly Regex MarkupTagRegex = new(@"</?[A-Za-z][^>]*>", RegexOptions.Compiled);
    private static string StripTags(string text) => MarkupTagRegex.Replace(text, "").Trim();

    // 納品の行: 「<品名> ×n（<ミッション名>）」。ミッション名がまだ分からなければ省いて出し、分かったら書き足す (ApplyMissionName)。
    // 在庫から引く品 (kind delivered。鍵は 時刻・MissionId・ObjectiveId、品名は通知の日本語のまま) を付ける
    private void OnDelivery(DateTime time, string body, string missionId, string objectiveId)
    {
        var m = DeliveryRegex.Match(body);
        if (!m.Success || m.Groups[2].Value != m.Groups[3].Value) return;
        var itemName = m.Groups[1].Value.Trim();
        var content = $"{itemName} ×{m.Groups[2].Value}";
        string? name = null;
        var known = missionId.Length > 0
                    && (_missionNames.TryGetValue(missionId, out name) || (_sharedMissionNames != null && _sharedMissionNames.TryGet(missionId, out name)));
        var count = int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        var e = Add(time, "納品", known ? $"{content}（{StripTags(name!)}）" : content,
            importItems: [new GameLogImportItem
            {
                Kind = GameLogImportItem.KindDelivered, LogKey = GameLogImportItem.MakeKey(LogTimeText(time), missionId, objectiveId),
                ItemClass = "", Quantity = count, DisplayName = itemName,
            }]);
        if (known || missionId.Length == 0) return;
        e.PendingMissionId = missionId;
        if (_sharedMissionNames != null) return;
        if (!_missionWaiting.TryGetValue(missionId, out var list)) _missionWaiting[missionId] = list = new();
        list.Add(e);
    }

    private static bool StartsWithAny(string text, string[] headings, out string rest)
    {
        foreach (var h in headings)
        {
            if (!text.StartsWith(h, StringComparison.Ordinal)) continue;
            rest = text[h.Length..];
            return true;
        }
        rest = "";
        return false;
    }

    // 見出しの後ろの本文。先頭の区切り (：/:/空白) と、本文が空のときに残る末尾の ": " を除く
    private static string Body(string rest) =>
        rest.TrimStart('：', ':', ' ', '\u00A0').TrimEnd().TrimEnd(':', '：').TrimEnd();

    // ─── 契約の終了 ───
    private void OnEndMission(DateTime time, string missionId, string completion)
    {
        var kind = completion switch
        {
            "Complete" => "契約完了",
            "Fail" => "契約失敗",
            "Abandon" => "契約放棄",
            _ => "契約終了",
        };
        string? name = null;
        var known = _missionNames.TryGetValue(missionId, out name)
                    || (_sharedMissionNames != null && _sharedMissionNames.TryGet(missionId, out name));
        var content = known ? name! : MissionShort(missionId);
        if (kind == "契約終了") content += $" ({completion})";
        var e = Add(time, kind, content);
        if (known) return;
        // 通知が EndMission の直後や別のファイルで来ることがあるので、名前が分かったら書き換える
        e.PendingMissionId = missionId;
        if (_sharedMissionNames != null) return;   // 共有の対応があるときは、書き換えは呼ぶ側 (GameLogMissionNames.Apply) が行う
        if (!_missionWaiting.TryGetValue(missionId, out var list)) _missionWaiting[missionId] = list = new();
        list.Add(e);
    }

    private void LearnMission(string missionId, string name)
    {
        if (missionId.Length == 0 || name.Length == 0) return;
        _missionNames[missionId] = name;
        if (_sharedMissionNames != null)
        {
            _sharedMissionNames.Set(missionId, name);
            return;
        }
        if (!_missionWaiting.Remove(missionId, out var list)) return;
        foreach (var e in list) ApplyMissionName(e, name);
        Changed = true;
    }

    // 名前待ちの契約終了の行に名前を入れる。"契約終了" の行は末尾に " (CompletionType)" が付いているので、先頭の ID だけを置き換える。
    // 納品の行 (名前を省いて出してある) は末尾に「（名前）」を書き足す
    public static void ApplyMissionName(GameLogEntry e, string name)
    {
        if (e.PendingMissionId is not { } id) return;
        if (e.Kind == "納品")
        {
            e.Content += $"（{StripTags(name)}）";
            e.PendingMissionId = null;
            return;
        }
        var shortId = MissionShort(id);
        e.Content = e.Content.StartsWith(shortId, StringComparison.Ordinal) ? name + e.Content[shortId.Length..] : name;
        e.PendingMissionId = null;
    }

    private static string MissionShort(string missionId) => missionId.Length > 8 ? missionId[..8] : missionId;

    // ─── 船 ───
    // 船のクラス名_ID → 表示名 (末尾の _数字 を除いて名前を引く)
    private string ShipDisplay(string classWithId)
    {
        var m = TrailingIdRegex.Match(classWithId);
        return _shipName(m.Success ? m.Groups[1].Value : classWithId);
    }

    private void LearnVehicleFromName(string classWithId)
    {
        var m = TrailingIdRegex.Match(classWithId);
        if (m.Success) LearnVehicle(m.Groups[2].Value, m.Groups[1].Value);
    }

    // ナビゲーション・ドッキング・境界違反などの行から、行の中のすべての クラス_ID[ID] / 'クラス_ID' [ID] を拾い、クラス名が船のものだけ覚える。
    // own = 自分の船だけが出る行 (OwnVehicleLearnTags) なら、自分の船としても覚える
    private void LearnVehiclesInLine(string line, bool own)
    {
        foreach (Match m in VehicleRefRegex.Matches(line)) LearnVehicleIfShip(m.Groups[2].Value, m.Groups[1].Value, own);
        foreach (Match m in QuotedVehicleRefRegex.Matches(line)) LearnVehicleIfShip(m.Groups[2].Value, m.Groups[1].Value, own);
    }

    private void LearnVehicleIfShip(string id, string className, bool own)
    {
        if (id.Length == 0 || className.Length == 0) return;
        if (_isVehicleClass(className)) LearnVehicle(id, className, own);
    }

    // 船の ID → クラス名を覚える (own なら自分の船としても)。船名待ちの呼び出しの行があれば書き換え、
    // 自分の船と分かるまで出していなかった格納の行があれば行にする
    private void LearnVehicle(string id, string className, bool own = true)
    {
        if (id.Length == 0 || className.Length == 0) return;
        _vehicleClasses[id] = className;
        if (own) _ownVehicles.Add(id);
        if (_vehicleWaiting.Remove(id, out var list))
        {
            var ship = _shipName(className);
            foreach (var (e, build) in list) e.Content = build(ship);
            Changed = true;
        }
        FlushHiddenVehicleRows(id);
    }

    // 自分の船の ID を覚える (呼び出しの行。クラス名はまだ分からない)。出していなかった格納の行があり、クラス名も分かっていれば行にする
    private void MarkOwnVehicle(string id)
    {
        if (id.Length == 0) return;
        _ownVehicles.Add(id);
        FlushHiddenVehicleRows(id);
    }

    // 出していなかった格納の行を、自分の船でクラス名も分かったときに行にする
    private void FlushHiddenVehicleRows(string id)
    {
        if (!_ownVehicles.Contains(id) || !_vehicleClasses.TryGetValue(id, out var cls)) return;
        if (!_vehicleHidden.Remove(id, out var hidden)) return;
        var ship = _shipName(cls);
        foreach (var (time, kind, placeId, build) in hidden) AttachPlace(Add(time, kind, build(ship)), placeId);
    }

    // 自分の船の ID の付いた行 (呼び出し)。クラス名が分かっていれば船名、まだなら「船 ID …」で出し、後で分かったら書き換える。
    // placeId はそのときの着陸場所の ID (名前がまだなら名前待ちにする。"0" なら無し)
    private void AddWithVehicle(DateTime time, string kind, string id, string placeId, Func<string, string> build)
    {
        if (_vehicleClasses.TryGetValue(id, out var cls))
        {
            AttachPlace(Add(time, kind, build(_shipName(cls))), placeId);
            MarkOwnVehicle(id);
            return;
        }
        var e = Add(time, kind, build($"船 ID {id}"));
        AttachPlace(e, placeId);
        if (!_vehicleWaiting.TryGetValue(id, out var list)) _vehicleWaiting[id] = list = new();
        list.Add((e, build));
        MarkOwnVehicle(id);
    }

    // 船の ID の付いた行のうち、自分の船でクラス名も分かるものだけ出すもの (格納)。まだ分からなければ覚えておき、分かったときに行にする
    private void AddWithVehicleIfKnown(DateTime time, string kind, string id, string placeId, Func<string, string> build)
    {
        if (_ownVehicles.Contains(id) && _vehicleClasses.TryGetValue(id, out var cls))
        {
            AttachPlace(Add(time, kind, build(_shipName(cls))), placeId);
            return;
        }
        if (!_vehicleHidden.TryGetValue(id, out var list)) _vehicleHidden[id] = list = new();
        list.Add((time, kind, placeId, build));
    }

    // ─── 共通 ───
    private GameLogEntry Add(DateTime time, string kind, string content, double? amount = null, GameLogPurchase? purchase = null,
        List<GameLogImportItem>? importItems = null, GameLogTrade? trade = null, GameLogMining? mining = null)
    {
        var e = new GameLogEntry
        {
            TimeUtc = time, Kind = kind, Content = content, Amount = amount, Source = _source, Purchase = purchase,
            ImportItems = importItems, Trade = trade, Mining = mining,
            Seq = System.Threading.Interlocked.Increment(ref _seq),
        };
        Entries.Add(e);
        Changed = true;
        return e;
    }

    private static double? ParseNumber(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string ShortGuid(string guid) => guid.Length > 8 ? guid[..8] : guid;

    // ─── ファイルの読み方 ───

    // UTF-8 (BOM なし)。不正なバイトは置換文字 (U+FFFD) になる
    public static readonly Encoding LogEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    // 過去のログ 1 ファイルを全部読む。ゲームが書き込み中でも開けるように共有で開く
    public static List<GameLogEntry> ParseFile(string path, string source, Func<string, string>? shipName = null, Func<string, string>? itemName = null,
        GameLogMissionNames? sharedMissionNames = null, Func<string, string?>? translate = null, Func<string, bool>? isVehicleClass = null,
        Func<string, string?>? resourceName = null, Func<string, string>? placeName = null, GameLogPlaceNames? sharedPlaceNames = null,
        Func<string, string>? itemLabel = null)
    {
        var parser = new GameLogParser(source, shipName, itemName, sharedMissionNames, translate, isVehicleClass, resourceName, placeName, sharedPlaceNames, itemLabel);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, LogEncoding, detectEncodingFromByteOrderMarks: false);
        string? line;
        while ((line = reader.ReadLine()) != null) parser.FeedLine(line);
        parser.Flush();
        return parser.Entries;
    }
}

// 書き込み中の Game.log を少しずつ読む。前回の位置を覚え、増えた分だけを読む。
// 行の途中で終わっている末尾は次回に回す。次のどちらかなら (ゲームの再起動) 最初から読み直す:
//   ・ファイルが前回より小さくなった
//   ・ファイルの先頭の 1 行が前回と違う。先頭の行は
//       <2026-10-12T12:18:59.045Z> BackupNameAttachment=" Build(12881860) 12 Oct 26 (21 18 53)"  -- used by backup system
//     のように起動した時刻が入るので、起動ごとに変わる (新しい Game.log が前回より長くなってから読んでも気づける)
public sealed class GameLogTail
{
    private const int HeadMaxBytes = 1024;   // 先頭の 1 行を探す長さ (改行がこの中に無ければ、まだ比べない)

    private readonly string _path;
    private readonly string _source;
    private readonly Func<string, string>? _shipName;
    private readonly Func<string, string>? _itemName;
    private readonly GameLogMissionNames? _sharedMissionNames;
    private readonly Func<string, string?>? _translate;
    private readonly Func<string, bool>? _isVehicleClass;
    private readonly Func<string, string?>? _resourceName;
    private readonly Func<string, string>? _placeName;
    private readonly GameLogPlaceNames? _sharedPlaceNames;
    private readonly Func<string, string>? _itemLabel;
    private long _position;
    private long _lastLength;
    private byte[]? _head;                   // 覚えている先頭の 1 行 (改行まで)。まだ分からなければ null
    private byte[] _buffer = new byte[1 << 20];   // 読み取りに使い回す
    private readonly byte[] _headBuffer = new byte[HeadMaxBytes];

    public GameLogParser Parser { get; private set; }

    public GameLogTail(string path, string source, Func<string, string>? shipName = null, Func<string, string>? itemName = null,
        GameLogMissionNames? sharedMissionNames = null, Func<string, string?>? translate = null, Func<string, bool>? isVehicleClass = null,
        Func<string, string?>? resourceName = null, Func<string, string>? placeName = null, GameLogPlaceNames? sharedPlaceNames = null,
        Func<string, string>? itemLabel = null)
    {
        _path = path;
        _source = source;
        _shipName = shipName;
        _itemName = itemName;
        _sharedMissionNames = sharedMissionNames;
        _translate = translate;
        _isVehicleClass = isVehicleClass;
        _resourceName = resourceName;
        _placeName = placeName;
        _sharedPlaceNames = sharedPlaceNames;
        _itemLabel = itemLabel;
        Parser = new GameLogParser(source, shipName, itemName, sharedMissionNames, translate, isVehicleClass, resourceName, placeName, sharedPlaceNames, itemLabel);
    }

    // 増えた分を読む。戻り値: ファイルがあったか。reset = 最初から読み直した (それまでの行は捨てた)
    public bool Poll(out bool reset)
    {
        reset = false;
        if (!File.Exists(_path)) return false;
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = fs.Length;

        var head = ReadHead(fs, length);
        var headChanged = head != null && _head != null && !head.AsSpan().SequenceEqual(_head.AsSpan());
        if (length < _lastLength || headChanged)
        {
            _position = 0;
            _lastLength = 0;
            Parser = new GameLogParser(_source, _shipName, _itemName, _sharedMissionNames, _translate, _isVehicleClass, _resourceName, _placeName, _sharedPlaceNames, _itemLabel);
            reset = true;
        }
        if (head != null) _head = head;

        // 前回から長さが変わっていない (行の途中の末尾だけが残っているときも含む) なら読まない
        if (!reset && length == _lastLength) return true;
        if (length <= _position)
        {
            _lastLength = length;
            return true;
        }

        fs.Seek(_position, SeekOrigin.Begin);
        var carry = 0;   // _buffer の先頭に残した、前の塊の行の途中
        while (_position + carry < length)
        {
            var want = (int)Math.Min(_buffer.Length - carry, length - _position - carry);
            if (want <= 0)
            {
                // 1 行が _buffer より長い: 広げる
                Array.Resize(ref _buffer, _buffer.Length * 2);
                continue;
            }
            var read = fs.Read(_buffer, carry, want);
            if (read <= 0) break;
            var total = carry + read;
            var lastNewline = Array.LastIndexOf(_buffer, (byte)'\n', total - 1, total);
            if (lastNewline < 0)
            {
                carry = total;
                continue;
            }
            var text = GameLogParser.LogEncoding.GetString(_buffer, 0, lastNewline + 1);
            foreach (var line in text.Split('\n'))
                if (line.Length > 0) Parser.FeedLine(line);
            _position += lastNewline + 1;
            carry = total - (lastNewline + 1);
            Buffer.BlockCopy(_buffer, lastNewline + 1, _buffer, 0, carry);
        }
        // 読み終えてから覚える (途中で例外になったら、次回もう一度読む)
        _lastLength = length;
        return true;
    }

    // 先頭の 1 行 (改行まで)。改行が先頭 HeadMaxBytes の中にまだ無ければ null
    private byte[]? ReadHead(FileStream fs, long length)
    {
        var want = (int)Math.Min(HeadMaxBytes, length);
        if (want <= 0) return null;
        fs.Seek(0, SeekOrigin.Begin);
        var got = 0;
        while (got < want)
        {
            var read = fs.Read(_headBuffer, got, want - got);
            if (read <= 0) break;
            got += read;
        }
        var newline = Array.IndexOf(_headBuffer, (byte)'\n', 0, got);
        return newline < 0 ? null : _headBuffer.AsSpan(0, newline + 1).ToArray();
    }
}
