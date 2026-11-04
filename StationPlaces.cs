using System.IO;

namespace StarCitizenJapaneseTextCreater;

// 場所の置き場所・短い名前・星図の場所の一覧 (2026-11-04、利用者の指示「2D 地図は消して」で、2D の地図の取り込み (StationMapExports) を消し、
// 3D (新しい方式) が使うものだけをここに移した)
internal static class StationPlaces
{
    // 置き場所: 3D の置き場所 (設定の Ship3DFolder) の stations。区画の置き方の一覧 (view3d_layout.db) を置く
    public static string Folder => Path.Combine(Ship3DExports.Folder, "stations");
    // ハンガーの表 (hangar_catalog.json。StationHangarCatalog) の置き場所
    public static string HangarFolder => Path.Combine(Folder, "hangars");
    // 場所の短い名前 (星図の場所の record の頭 StarMapObject. を外したもの。ハンガーは StationHangars.ShortOf)
    public static string ShortName(string record) =>
        StationHangars.IsRecord(record) ? StationHangars.ShortOf(StationHangars.SocpakOf(record))
        : record.StartsWith("StarMapObject.", StringComparison.OrdinalIgnoreCase) ? record["StarMapObject.".Length..] : record;

    // gamedata_cache.db の starmap_locations (record_name・uuid・type・system と英語名)。恒星 (Star) も含めて全部 (StationIndex.FindRoots が恒星から辿るため)。
    // 表・uuid の列が無ければ (インデックス構築前) 例外
    internal static List<(StationPlace Place, string Name)> LoadPlaces(string dbPath)
    {
        if (!File.Exists(dbPath)) throw new InvalidOperationException("ゲームデータの DB がありません (設定タブの［インデックス構築］が要ります)");
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('starmap_locations') WHERE name IN ('record_name', 'uuid', 'type', 'system', 'name_en')";
        if (Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) != 5) throw new InvalidOperationException("星図の場所の表がありません (設定タブの［インデックス構築］が要ります)");
        cmd.CommandText = "SELECT record_name, uuid, type, system, name_en FROM starmap_locations";
        var list = new List<(StationPlace, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((new StationPlace(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)), r.GetString(4)));
        return list;
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
        // (2026-11-03、親の決定) ハンガーの中の船のエレベーターの台 (StationTree.SplitShipElevators)。アイコンは Markers の中から形の近いもの (自分の乗り物) を選んだ
        new("shipelev", "船のエレベーター", "marker_icon_player_vehicle.svg", "#3949ab"),
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

// 場所の一覧の決まり (2026-11-04、利用者の指示「2D 地図は消して」で、2D の地図の窓 (StationMapWindow) から、契約の行き先の地図・星図に載っていない場所の名前に使うものを移した)。
// (2026-11-04、利用者の決定「やめる」) 床のメッシュの有無 (has_floor) の調べと「（床なし）」の印はやめた
internal static class StationPlaceList
{
    // 種類のチェックの種類 (表の順)。名前は［詳細］の場所の種類 (ShopTree.TypeLabels) に合わせる
    private const string KindStation = "ステーション・都市", KindOutpost = "前哨基地", KindAsteroid = "小惑星";
    // (2026-11-01、利用者の指示「ミッションで使われるマップは全部出すべき」・「1 A」) 足した種類。言葉は［詳細］の場所の種類 (ShopTree.TypeLabel) と、
    // 星図に無い種類のゲームの訳 (StationPlace.ExtraTypeLabels: 洞窟・残骸・墜落現場)
    private const string KindCave = "洞窟", KindWreck = "残骸", KindCrash = "墜落現場", KindPoi = "注目地点", KindAnomaly = "異常域", KindJump = "ジャンプポイント", KindNav = "航行ポイント";
    // 洞窟・残骸・墜落現場は、今の床の決め方では床ができず空の地図になるので、v2.5.0 では一覧にもチェックにも出さない (2026-11-01、利用者「次バージョンでもよい」)。
    // ルート (FindRoots の規則 1) は見つけたままにしてあり、HiddenKinds から外せば出る
    private static readonly HashSet<string> HiddenKinds = new(StringComparer.Ordinal) { KindCave, KindWreck, KindCrash };

    internal static string? KindOfType(string type) =>
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

    // 一覧に出す場所か (契約の詳細の地図のリンク): 種類のある種類で、出さない種類 (HiddenKinds) でないもの
    internal static bool IsListedPlace(string type) => KindOfType(type) is { } kind && !HiddenKinds.Contains(kind);

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
}
