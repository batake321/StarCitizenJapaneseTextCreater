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
    // 店の購入の行 (種類「購入」「購入失敗」) の依頼の中身。ほかの行は null
    public GameLogPurchase? Purchase { get; init; }
    // ［ゲームログ］タブが決める「装備に入れる」ボタンの状態 (UI のスレッドだけで使う)
    public PurchaseImportState ImportState { get; set; }
}

// ［ゲームログ］の購入の行の「装備に入れる」ボタンの状態 (HangarService.ClassifyPurchase)
public enum PurchaseImportState { None, Ready, Imported }

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
    // まだ出していない格納の行 (ID → (時刻, 種類, 内容の組み立て))。格納は周りの着陸場で他人の船が格納されたときにも出るので、
    // 自分の船 (_ownVehicles) でクラス名も分かる (_vehicleClasses) まで表には出さず、分かったときに行にする
    private readonly Dictionary<string, List<(DateTime Time, string Kind, Func<string, string> Build)>> _vehicleHidden = new(StringComparer.Ordinal);

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

    public GameLogParser(string source, Func<string, string>? shipName = null, Func<string, string>? itemName = null,
        GameLogMissionNames? sharedMissionNames = null, Func<string, string?>? translate = null, Func<string, bool>? isVehicleClass = null,
        Func<string, string?>? resourceName = null)
    {
        _source = source;
        _shipName = shipName ?? (s => s);
        _itemName = itemName ?? (s => s);
        _sharedMissionNames = sharedMissionNames;
        _translate = translate ?? (_ => null);
        _isVehicleClass = isVehicleClass ?? (_ => true);
        _resourceName = resourceName ?? (_ => null);
    }

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
        "<Vehicle Control Flow>",   // 船の ID → クラス名を覚えるだけ (releasing control token for 'クラス_ID' [ID])
        .. VehicleLearnTags,
        .. InventoryTags,
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
    private static readonly Regex NotificationRegex = new(@"<SHUDEvent_OnNotification> Added notification ""(.*)"" \[\d+\] to queue\.(?:.*?MissionId: \[([0-9A-Fa-f-]*)\])?", RegexOptions.Compiled);
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
            if ((m = ControlTokenRegex.Match(line)).Success) LearnVehicle(m.Groups[2].Value, m.Groups[1].Value);
            return;
        }
        if (HasAnyTag(line, VehicleLearnTags))
        {
            LearnVehiclesInLine(line, own: HasAnyTag(line, OwnVehicleLearnTags));
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
            OnNotification(time, m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : "");
            return;
        }
        if ((m = NotificationCutRegex.Match(line)).Success)
        {
            OnNotification(time, m.Groups[1].Value, "");
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
            var e = Add(time, "購入", $"{_itemName(item)} × {m.Groups[4].Value} / 店: {m.Groups[1].Value}", ParseNumber(m.Groups[2].Value), purchase);
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
            // quantity は cSCU (1/100 SCU) なので SCU に直す
            var scu = ParseNumber(m.Groups[4].Value) is double c ? (c / 100).ToString("#,0.##", CultureInfo.InvariantCulture) : m.Groups[4].Value;
            var e = Add(time, "商品購入", $"{ResourceDisplay(m.Groups[3].Value)} {scu} SCU / 店: {m.Groups[1].Value}", ParseNumber(m.Groups[2].Value));
            _pendingCommodity["Buying"] = e;
            return;
        }
        if ((m = CommoditySellRegex.Match(line)).Success)
        {
            // 売りの quantity には単位が付かない (実ログの全行で quantity[数] のみ) ので数値のまま出す
            var e = Add(time, "商品売却", $"{ResourceDisplay(m.Groups[3].Value)} × {m.Groups[4].Value} / 店: {m.Groups[1].Value}", ParseNumber(m.Groups[2].Value));
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
        if ((m = SpawnRegex.Match(line)).Success)
        {
            var area = m.Groups[2].Value.Trim();
            AddWithVehicle(time, "呼び出し", m.Groups[1].Value, ship => $"{ship} / 着陸場所: {area}");
            return;
        }
        if ((m = SpawnedRegex.Match(line)).Success)
        {
            // 呼び出しの完了 (同じ呼び出しで SetVehicleSpawningInformations の数秒後に出る)。呼び出しと同じく自分の船なので船名待ちで出す
            var area = m.Groups[2].Value.Trim();
            AddWithVehicle(time, "呼び出し完了", m.Groups[1].Value, ship => $"{ship} / 着陸場所: {area}");
            return;
        }
        if ((m = StowRegex.Match(line)).Success)
        {
            var area = m.Groups[1].Value;
            AddWithVehicleIfKnown(time, "格納", m.Groups[2].Value, ship => $"{ship} / {area}");
            return;
        }
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
        if ((m = MineralAttachedRegex.Match(line)).Success)
        {
            var cls = m.Groups[1].Value;
            var id = m.Groups[2].Value;
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
            if (!_mineCandidates.Remove(m.Groups[2].Value, out var c)) return;
            var e = Add(c.Time, "採掘", $"{MineralName(c.Class)} ×1");
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

    private void OnNotification(DateTime time, string text, string missionId)
    {
        if (missionId.Trim('0', '-').Length == 0) missionId = "";   // 00000000-0000-… はミッション無し

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
            Add(time, kind, Body(text[heading.Length..]));
            return;
        }
        // 区域: 見出し (": " の前) そのもの
        var colon = text.IndexOf(": ", StringComparison.Ordinal);
        var title = (colon >= 0 ? text[..colon] : text).Trim().TrimEnd(':').Trim();
        if (title.EndsWith("管轄区域に進入", StringComparison.Ordinal) || Array.IndexOf(AreaHeadings, title) >= 0
            || StartsWithAny(title, AreaPrefixHeadings, out _))
            Add(time, "区域", title);
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

    // 名前待ちの契約終了の行に名前を入れる。"契約終了" の行は末尾に " (CompletionType)" が付いているので、先頭の ID だけを置き換える
    public static void ApplyMissionName(GameLogEntry e, string name)
    {
        if (e.PendingMissionId is not { } id) return;
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
        foreach (var (time, kind, build) in hidden) Add(time, kind, build(ship));
    }

    // 自分の船の ID の付いた行 (呼び出し)。クラス名が分かっていれば船名、まだなら「船 ID …」で出し、後で分かったら書き換える
    private void AddWithVehicle(DateTime time, string kind, string id, Func<string, string> build)
    {
        if (_vehicleClasses.TryGetValue(id, out var cls))
        {
            Add(time, kind, build(_shipName(cls)));
            MarkOwnVehicle(id);
            return;
        }
        var e = Add(time, kind, build($"船 ID {id}"));
        if (!_vehicleWaiting.TryGetValue(id, out var list)) _vehicleWaiting[id] = list = new();
        list.Add((e, build));
        MarkOwnVehicle(id);
    }

    // 船の ID の付いた行のうち、自分の船でクラス名も分かるものだけ出すもの (格納)。まだ分からなければ覚えておき、分かったときに行にする
    private void AddWithVehicleIfKnown(DateTime time, string kind, string id, Func<string, string> build)
    {
        if (_ownVehicles.Contains(id) && _vehicleClasses.TryGetValue(id, out var cls))
        {
            Add(time, kind, build(_shipName(cls)));
            return;
        }
        if (!_vehicleHidden.TryGetValue(id, out var list)) _vehicleHidden[id] = list = new();
        list.Add((time, kind, build));
    }

    // ─── 共通 ───
    private GameLogEntry Add(DateTime time, string kind, string content, double? amount = null, GameLogPurchase? purchase = null)
    {
        var e = new GameLogEntry
        {
            TimeUtc = time, Kind = kind, Content = content, Amount = amount, Source = _source, Purchase = purchase,
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
        Func<string, string?>? resourceName = null)
    {
        var parser = new GameLogParser(source, shipName, itemName, sharedMissionNames, translate, isVehicleClass, resourceName);
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
    private long _position;
    private long _lastLength;
    private byte[]? _head;                   // 覚えている先頭の 1 行 (改行まで)。まだ分からなければ null
    private byte[] _buffer = new byte[1 << 20];   // 読み取りに使い回す
    private readonly byte[] _headBuffer = new byte[HeadMaxBytes];

    public GameLogParser Parser { get; private set; }

    public GameLogTail(string path, string source, Func<string, string>? shipName = null, Func<string, string>? itemName = null,
        GameLogMissionNames? sharedMissionNames = null, Func<string, string?>? translate = null, Func<string, bool>? isVehicleClass = null,
        Func<string, string?>? resourceName = null)
    {
        _path = path;
        _source = source;
        _shipName = shipName;
        _itemName = itemName;
        _sharedMissionNames = sharedMissionNames;
        _translate = translate;
        _isVehicleClass = isVehicleClass;
        _resourceName = resourceName;
        Parser = new GameLogParser(source, shipName, itemName, sharedMissionNames, translate, isVehicleClass, resourceName);
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
            Parser = new GameLogParser(_source, _shipName, _itemName, _sharedMissionNames, _translate, _isVehicleClass, _resourceName);
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
